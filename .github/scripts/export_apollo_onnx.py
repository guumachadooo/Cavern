#!/usr/bin/env python3
"""Exports JusperLee/Apollo (CC BY-SA 4.0) to pure float32 ONNX opset 17 (no complex dtypes)
and mirrors FlashSR 48 kHz ONNX (Apache-2.0) with SHA-256 verification."""

import copy
import hashlib
import inspect
import math
import os
import subprocess
import sys
import urllib.request
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
import torch
import torch.nn as nn
import torch.nn.functional as F
from huggingface_hub import hf_hub_download


def ensure_apollo_repo(workdir: Path) -> Path:
    repo_dir = workdir / "Apollo"
    if not repo_dir.exists():
        subprocess.check_call(
            ["git", "clone", "--depth", "1", "https://github.com/JusperLee/Apollo.git", str(repo_dir)]
        )
    return repo_dir


def _build_stft_kernels(n_fft: int):
    n_bins = n_fft // 2 + 1
    window = torch.hann_window(n_fft, periodic=True, dtype=torch.float64)
    n = torch.arange(n_fft, dtype=torch.float64)[None, :]  # [1, N]
    k = torch.arange(n_bins, dtype=torch.float64)[:, None]  # [K, 1]
    angles = 2.0 * math.pi * k * n / float(n_fft)

    # Forward DFT weights [2*K, 1, N]
    fwd_re = torch.cos(angles) * window[None, :]
    fwd_im = -torch.sin(angles) * window[None, :]
    fwd_kernel = torch.cat([fwd_re, fwd_im], dim=0).unsqueeze(1).float()

    # Inverse rFFT weights [2*K, 1, N] (DC and Nyquist weighted 1/N, interior bins 2/N)
    scale = torch.full((n_bins, 1), 2.0 / float(n_fft), dtype=torch.float64)
    scale[0, 0] = 1.0 / float(n_fft)
    if n_fft % 2 == 0:
        scale[-1, 0] = 1.0 / float(n_fft)

    inv_re = scale * torch.cos(angles) * window[None, :]
    inv_im = -scale * torch.sin(angles) * window[None, :]
    inv_kernel = torch.cat([inv_re, inv_im], dim=0).unsqueeze(1).float()

    win_sq_kernel = (window * window).view(1, 1, n_fft).float()
    return fwd_kernel, inv_kernel, win_sq_kernel


class OnnxSafeRoformerAttention(nn.Module):
    """Drop-in replacement for Apollo's Roformer without dynamic numpy allocation or SDPA fused ops."""

    def __init__(self, orig_roformer: nn.Module):
        super().__init__()
        self.input_size = orig_roformer.input_size
        self.hidden_size = orig_roformer.hidden_size
        self.num_head = orig_roformer.num_head
        self.window = orig_roformer.window
        self.scale = 1.0 / math.sqrt(float(self.hidden_size))

        self.register_buffer("cos_freq", orig_roformer.cos_freq.detach().clone())
        self.register_buffer("sin_freq", orig_roformer.sin_freq.detach().clone())
        self.input_norm = orig_roformer.input_norm
        self.weight = orig_roformer.weight
        self.output = orig_roformer.output
        self.MLP = orig_roformer.MLP
        self.MLP_output = orig_roformer.MLP_output

    def _apply_rope(self, x: torch.Tensor) -> torch.Tensor:
        # x: [B, H, T, D]
        T, D = x.shape[-2], x.shape[-1]
        flat = x.reshape(-1, T, D)
        cos = self.cos_freq[:T].unsqueeze(0)
        sin = self.sin_freq[:T].unsqueeze(0)
        even = flat[..., 0::2]
        odd = flat[..., 1::2]
        rot = torch.stack((-odd, even), dim=-1).reshape(-1, T, D)
        return (flat * cos + rot * sin).reshape(x.shape)

    def forward(self, x: torch.Tensor):
        # x: [B, N, T]
        B, _, T = x.shape
        qkv = self.weight(self.input_norm(x))
        qkv = qkv.reshape(B, self.num_head, self.hidden_size * 3, T).transpose(-1, -2)
        q, k, v = torch.split(qkv, self.hidden_size, dim=-1)

        q = self._apply_rope(q)
        k = self._apply_rope(k)

        scores = torch.matmul(q, k.transpose(-2, -1)) * self.scale
        attn = F.softmax(scores, dim=-1)
        context = torch.matmul(attn, v).transpose(-1, -2).reshape(B, -1, T)

        out = self.output(context) + x
        gate, z = self.MLP(out).chunk(2, dim=1)
        out = out + self.MLP_output(F.silu(gate) * z)
        return out, (k, v)


class ApolloOnnxWrapper(nn.Module):
    """Pure real-valued opset-17 ONNX wrapper matching JusperLee/Apollo forward()."""

    def __init__(self, apollo: nn.Module):
        super().__init__()
        self.win = int(apollo.win)
        self.stride = int(apollo.stride)
        self.pad = self.win // 2
        self.enc_dim = int(apollo.enc_dim)
        self.eps = float(apollo.eps)
        self.band_width = [int(b) for b in apollo.band_width]
        self.nband = len(self.band_width)

        fwd_k, inv_k, win_sq_k = _build_stft_kernels(self.win)
        self.register_buffer("stft_fwd_kernel", fwd_k)
        self.register_buffer("stft_inv_kernel", inv_k)
        self.register_buffer("win_sq_kernel", win_sq_kernel)

        self.BN = apollo.BN
        self.net = apollo.net
        for block in self.net:
            block.band_net = OnnxSafeRoformerAttention(block.band_net)
        self.output = apollo.output

    def forward(self, mix: torch.Tensor) -> torch.Tensor:
        # mix: [B, C, N]
        B, C, N = mix.shape
        flat = mix.reshape(B * C, N)

        # Match torch.stft(center=True, pad_mode='reflect')
        padded = F.pad(flat.unsqueeze(1), (self.pad, self.pad), mode="reflect")
        spec_ri = F.conv1d(padded, self.stft_fwd_kernel, stride=self.stride)  # [BC, 2*F, T]
        spec_re = spec_ri[:, : self.enc_dim, :]
        spec_im = spec_ri[:, self.enc_dim :, :]

        subband_features = []
        band_idx = 0
        for i in range(self.nband):
            bw = self.band_width[i]
            re_i = spec_re[:, band_idx : band_idx + bw, :]
            im_i = spec_im[:, band_idx : band_idx + bw, :]
            power = torch.sqrt((re_i * re_i + im_i * im_i).sum(dim=1, keepdim=True) + self.eps)
            inv_power = 1.0 / power
            feat_in = torch.cat([re_i * inv_power, im_i * inv_power, torch.log(power)], dim=1)
            subband_features.append(self.BN[i](feat_in))
            band_idx += bw

        features = self.net(torch.stack(subband_features, dim=1))  # [B*C, nband, feat_dim, T]

        est_re = []
        est_im = []
        for i in range(self.nband):
            bw = self.band_width[i]
            ri = self.output[i](features[:, i]).reshape(B * C, 2, bw, -1)
            est_re.append(ri[:, 0])
            est_im.append(ri[:, 1])

        full_ri = torch.cat(est_re + est_im, dim=1)  # [B*C, 2*F, T]
        ola = F.conv_transpose1d(full_ri, self.stft_inv_kernel, stride=self.stride)  # [B*C, 1, L_out]

        ones_t = torch.ones_like(full_ri[:1, :1, :])
        env = F.conv_transpose1d(ones_t, self.win_sq_kernel, stride=self.stride).clamp(min=1e-11)

        out = (ola[:, 0, self.pad : self.pad + N] / env[0, 0, self.pad : self.pad + N]).reshape(B, C, N)
        return out


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        while chunk := f.read(1 << 20):
            h.update(chunk)
    return h.hexdigest()


def main():
    out_dir = Path(sys.argv[1] if len(sys.argv) > 1 else "dist_models").resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    work_dir = out_dir / "_work"
    work_dir.mkdir(parents=True, exist_ok=True)

    apollo_repo = ensure_apollo_repo(work_dir)
    sys.path.insert(0, str(apollo_repo))
    from look2hear.models import BaseModel  # type: ignore

    ckpt_path = hf_hub_download(repo_id="JusperLee/Apollo", filename="pytorch_model.bin")
    print(f"Loaded JusperLee/Apollo checkpoint from {ckpt_path}")

    native_model = BaseModel.from_pretrain(
        ckpt_path,
        sr=44100,
        win=20,
        feature_dim=256,
        layer=6,
    ).cpu().eval()

    # Copy before wrapping in-place so reference model stays untouched
    wrapper = ApolloOnnxWrapper(copy.deepcopy(native_model)).cpu().eval()

    sample_shapes = [(1, 2, 131072), (1, 2, 88200)]
    torch.manual_seed(42)
    dummy = torch.randn(*sample_shapes[0], dtype=torch.float32) * 0.25

    apollo_onnx_path = out_dir / "apollo_band_split_44k.onnx"
    export_kwargs = dict(
        export_params=True,
        opset_version=17,
        do_constant_folding=True,
        input_names=["mix"],
        output_names=["restored"],
        dynamic_axes={
            "mix": {0: "batch", 2: "samples"},
            "restored": {0: "batch", 2: "samples"},
        },
    )
    if "dynamo" in inspect.signature(torch.onnx.export).parameters:
        export_kwargs["dynamo"] = False

    with torch.inference_mode():
        torch.onnx.export(wrapper, dummy, str(apollo_onnx_path), **export_kwargs)

    model_proto = onnx.load(str(apollo_onnx_path))
    onnx.checker.check_model(model_proto)

    sess = ort.InferenceSession(str(apollo_onnx_path), providers=["CPUExecutionProvider"])
    for shape in sample_shapes:
        x = (torch.randn(*shape, dtype=torch.float32) * 0.25).contiguous()
        with torch.inference_mode():
            ref = native_model(x).numpy()
        got = sess.run(["restored"], {"mix": x.numpy()})[0]
        assert got.shape == ref.shape, f"Shape mismatch: {got.shape} != {ref.shape}"
        max_diff = float(np.max(np.abs(ref - got)))
        mean_diff = float(np.mean(np.abs(ref - got)))
        print(f"Parity check {shape}: max_abs_diff={max_diff:.6e}, mean_abs_diff={mean_diff:.6e}")
        if not np.isfinite(got).all() or max_diff >= 1e-3:
            raise RuntimeError(f"Apollo ONNX parity check failed for {shape}: max_abs_diff={max_diff}")

    flashsr_path = out_dir / "flashsr_48k.onnx"
    flashsr_url = "https://huggingface.co/YatharthS/FlashSR/resolve/main/onnx/model.onnx"
    req = urllib.request.Request(flashsr_url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=60) as resp:
        flashsr_bytes = resp.read()
    if len(flashsr_bytes) < 65536 or flashsr_bytes[:1] in (b"<", b"{"):
        raise RuntimeError(f"Invalid FlashSR download ({len(flashsr_bytes)} bytes)")
    flashsr_path.write_bytes(flashsr_bytes)
    onnx.checker.check_model(onnx.load(str(flashsr_path)))

    sums_path = out_dir / "SHA256SUMS.txt"
    lines = []
    for artifact in (apollo_onnx_path, flashsr_path):
        size_mb = artifact.stat().st_size / (1024.0 * 1024.0)
        digest = sha256_file(artifact)
        lines.append(f"{digest}  {artifact.name}")
        print(f"Artifact: {artifact.name} | {size_mb:.2f} MB | sha256={digest}")
    sums_path.write_text("\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
