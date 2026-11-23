using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

using Android.Content;

using Cavernize.Logic.Spatial;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Parses the extracted <c>onnxruntime_c_api.h</c> header to build an exact 0-based vtable slot map for
/// <c>struct OrtApi</c> across any ONNX Runtime version (1.17–1.31+) without hardcoded fragile offsets.
/// Evaluates Android ARM64 C preprocessor conditionals (<c>#ifdef _WIN32</c>, <c>#if</c>, <c>#else</c>, <c>#endif</c>),
/// handles multi-line macro declarations with nested parentheses, and validates canonical anchor slots.
/// </summary>
public static class OnnxRuntimeHeaderParser {
    static readonly Regex ApiVersionRegex = new(@"#\s*define\s+ORT_API_VERSION\s+(\d+)", RegexOptions.Compiled);
    static readonly Regex StructHeaderRegex = new(@"\bstruct\s+OrtApi\s*\{", RegexOptions.Compiled);
    static readonly Regex MacroStartRegex = new(@"\b(ORT_CLASS_RELEASE|ORT_API2_STATUS|ORT_API_STATUS|ORT_API_T|ORT_API)\s*\(", RegexOptions.Compiled);
    static readonly Regex RawFnPtrRegex = new(@"\(\s*(?:[A-Za-z0-9_]+\s*)*\*\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)\s*\(", RegexOptions.Compiled);

    static readonly HashSet<string> AndroidDefinedMacros = new(StringComparer.Ordinal) {
        "__ANDROID__",
        "ANDROID",
        "__linux__",
        "__unix__",
        "__aarch64__",
        "__LP64__"
    };

    /// <summary>
    /// Extracts the advertised <c>ORT_API_VERSION</c> and a validated map from C API function name
    /// (e.g. <c>CreateSession</c>, <c>ReleaseSession</c>) to its 0-based pointer slot index inside <c>struct OrtApi</c>.
    /// </summary>
    public static (uint ApiVersion, Dictionary<string, int> SlotMap) ParseOrtApiHeader(string headerText) {
        Dictionary<string, int> slots = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(headerText)) {
            return (17u, slots);
        }

        string noComments = StripCommentsPreservingNewlines(headerText);
        string preprocessed = PreprocessForAndroidArm64(noComments);

        uint apiVersion = 17u;
        Match verMatch = ApiVersionRegex.Match(preprocessed);
        if (verMatch.Success && uint.TryParse(verMatch.Groups[1].Value, out uint parsed)) {
            apiVersion = parsed;
        }

        Match structMatch = StructHeaderRegex.Match(preprocessed);
        if (!structMatch.Success) {
            return (apiVersion, slots);
        }

        int openBrace = preprocessed.IndexOf('{', structMatch.Index);
        int closeBrace = FindMatchingDelimiter(preprocessed, openBrace, '{', '}');
        if (openBrace < 0 || closeBrace <= openBrace) {
            return (apiVersion, slots);
        }

        string body = preprocessed.Substring(openBrace + 1, closeBrace - openBrace - 1);
        List<string> statements = SplitTopLevelStatements(body);

        int slotIndex = 0;
        for (int i = 0; i < statements.Count; i++) {
            string stmt = statements[i];
            if (string.IsNullOrWhiteSpace(stmt) || stmt.StartsWith('#')) {
                continue;
            }
            string symbol = TryExtractMemberSymbol(stmt);
            if (!string.IsNullOrEmpty(symbol)) {
                slots[symbol] = slotIndex++;
            }
        }

        // Validate canonical immutable OrtApi anchor slots (v1..v40+ ABI guarantee)
        if (!ValidateAnchorSlots(slots, out _)) {
            slots.Clear();
        }

        return (apiVersion, slots);
    }

    /// <summary>
    /// Diagnostic variant of <see cref="ParseOrtApiHeader"/> returning the failure reason when anchor validation fails.
    /// </summary>
    public static (uint ApiVersion, Dictionary<string, int> SlotMap, string Diagnostic) ParseOrtApiHeaderWithDiagnostics(string headerText) {
        Dictionary<string, int> slots = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(headerText)) {
            return (17u, slots, "header asset is empty or missing");
        }

        string noComments = StripCommentsPreservingNewlines(headerText);
        string preprocessed = PreprocessForAndroidArm64(noComments);

        uint apiVersion = 17u;
        Match verMatch = ApiVersionRegex.Match(preprocessed);
        if (verMatch.Success && uint.TryParse(verMatch.Groups[1].Value, out uint parsed)) {
            apiVersion = parsed;
        }

        Match structMatch = StructHeaderRegex.Match(preprocessed);
        if (!structMatch.Success) {
            return (apiVersion, slots, $"struct OrtApi not found (headerLen={headerText.Length})");
        }

        int openBrace = preprocessed.IndexOf('{', structMatch.Index);
        int closeBrace = FindMatchingDelimiter(preprocessed, openBrace, '{', '}');
        if (openBrace < 0 || closeBrace <= openBrace) {
            return (apiVersion, slots, $"struct OrtApi braces unbalanced (headerLen={headerText.Length})");
        }

        string body = preprocessed.Substring(openBrace + 1, closeBrace - openBrace - 1);
        List<string> statements = SplitTopLevelStatements(body);

        int slotIndex = 0;
        for (int i = 0; i < statements.Count; i++) {
            string stmt = statements[i];
            if (string.IsNullOrWhiteSpace(stmt) || stmt.StartsWith('#')) {
                continue;
            }
            string symbol = TryExtractMemberSymbol(stmt);
            if (!string.IsNullOrEmpty(symbol)) {
                slots[symbol] = slotIndex++;
            }
        }

        if (!ValidateAnchorSlots(slots, out string reason)) {
            int rawCount = slots.Count;
            slots.Clear();
            return (apiVersion, slots, $"anchor validation failed ({reason}, parsedSlots={rawCount}, stmts={statements.Count}, apiVer={apiVersion})");
        }

        return (apiVersion, slots, string.Empty);
    }

    static bool ValidateAnchorSlots(Dictionary<string, int> slots, out string reason) {
        if (slots.Count < 20) {
            reason = $"too few slots ({slots.Count})";
            return false;
        }
        if (!slots.TryGetValue("CreateStatus", out int s0) || s0 != 0) {
            reason = $"CreateStatus={(!slots.ContainsKey("CreateStatus") ? "missing" : s0.ToString())}";
            return false;
        }
        if (!slots.TryGetValue("GetErrorCode", out int s1) || s1 != 1) {
            reason = $"GetErrorCode={(!slots.ContainsKey("GetErrorCode") ? "missing" : s1.ToString())}";
            return false;
        }
        if (!slots.TryGetValue("GetErrorMessage", out int s2) || s2 != 2) {
            reason = $"GetErrorMessage={(!slots.ContainsKey("GetErrorMessage") ? "missing" : s2.ToString())}";
            return false;
        }
        if (!slots.TryGetValue("CreateEnv", out int s3) || s3 != 3) {
            reason = $"CreateEnv={(!slots.ContainsKey("CreateEnv") ? "missing" : s3.ToString())}";
            return false;
        }
        if (!slots.ContainsKey("CreateSession") || !slots.ContainsKey("Run") ||
            !slots.ContainsKey("ReleaseSession") || !slots.ContainsKey("ReleaseStatus")) {
            reason = "missing core CreateSession/Run/Release slots";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    static string TryExtractMemberSymbol(string stmt) {
        Match m = MacroStartRegex.Match(stmt);
        if (m.Success) {
            string macro = m.Groups[1].Value;
            int openParen = stmt.IndexOf('(', m.Index);
            int closeParen = FindMatchingDelimiter(stmt, openParen, '(', ')');
            if (openParen >= 0 && closeParen > openParen) {
                string argsText = stmt.Substring(openParen + 1, closeParen - openParen - 1);
                List<string> args = SplitTopLevelCommas(argsText);
                if (macro == "ORT_CLASS_RELEASE") {
                    string cls = args.Count >= 1 ? CleanIdentifier(args[0]) : "";
                    return string.IsNullOrEmpty(cls) ? null : "Release" + cls;
                }
                if (macro == "ORT_API2_STATUS" || macro == "ORT_API_STATUS") {
                    // ORT_API2_STATUS(Name, ...) -> Name is argument 0
                    return args.Count >= 1 ? CleanIdentifier(args[0]) : null;
                }
                // ORT_API(ReturnType, Name, ...) / ORT_API_T(ReturnType, Name, ...) -> Name is argument 1
                return args.Count >= 2 ? CleanIdentifier(args[1]) : null;
            }
        }

        Match raw = RawFnPtrRegex.Match(stmt);
        if (raw.Success) {
            return CleanIdentifier(raw.Groups[1].Value);
        }
        return null;
    }

    static List<string> SplitTopLevelStatements(string body) {
        List<string> results = [];
        StringBuilder current = new(128);
        int parenDepth = 0;
        int braceDepth = 0;

        for (int i = 0; i < body.Length; i++) {
            char c = body[i];
            if (c == '(') parenDepth++;
            else if (c == ')' && parenDepth > 0) parenDepth--;
            else if (c == '{') braceDepth++;
            else if (c == '}' && braceDepth > 0) braceDepth--;

            if (c == ';' && parenDepth == 0 && braceDepth == 0) {
                string s = current.ToString().Trim();
                if (!string.IsNullOrEmpty(s)) {
                    results.Add(s);
                }
                current.Clear();
            } else {
                current.Append(c);
            }
        }
        string tail = current.ToString().Trim();
        if (!string.IsNullOrEmpty(tail)) {
            results.Add(tail);
        }
        return results;
    }

    static List<string> SplitTopLevelCommas(string text) {
        List<string> parts = [];
        int parenDepth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++) {
            char c = text[i];
            if (c == '(') parenDepth++;
            else if (c == ')' && parenDepth > 0) parenDepth--;
            else if (c == ',' && parenDepth == 0) {
                parts.Add(text.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }
        parts.Add(text.Substring(start).Trim());
        return parts;
    }

    static int FindMatchingDelimiter(string text, int openIndex, char openChar, char closeChar) {
        if (openIndex < 0 || openIndex >= text.Length || text[openIndex] != openChar) {
            return -1;
        }
        int depth = 0;
        for (int i = openIndex; i < text.Length; i++) {
            char c = text[i];
            if (c == openChar) {
                depth++;
            } else if (c == closeChar) {
                depth--;
                if (depth == 0) {
                    return i;
                }
            }
        }
        return -1;
    }

    static string CleanIdentifier(string token) {
        if (string.IsNullOrWhiteSpace(token)) {
            return null;
        }
        string t = token.Trim();
        // Extract trailing valid C identifier if decorated (e.g. "ORT_API_CALL CreateStatus")
        int end = t.Length - 1;
        while (end >= 0 && !IsIdentChar(t[end])) end--;
        if (end < 0) return null;
        int start = end;
        while (start > 0 && IsIdentChar(t[start - 1])) start--;
        return t.Substring(start, end - start + 1);
    }

    static bool IsIdentChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';

    static string StripCommentsPreservingNewlines(string text) {
        StringBuilder sb = new(text.Length);
        int i = 0;
        int n = text.Length;
        while (i < n) {
            if (i + 1 < n && text[i] == '/' && text[i + 1] == '*') {
                i += 2;
                while (i + 1 < n && !(text[i] == '*' && text[i + 1] == '/')) {
                    if (text[i] == '\n') sb.Append('\n');
                    i++;
                }
                i = Math.Min(n, i + 2);
                sb.Append(' ');
            } else if (i + 1 < n && text[i] == '/' && text[i + 1] == '/') {
                i += 2;
                while (i < n && text[i] != '\n') i++;
            } else {
                sb.Append(text[i++]);
            }
        }
        return sb.ToString();
    }

    struct PreprocFrame {
        public bool ParentActive;
        public bool BranchActive;
        public bool BranchTaken;
    }

    static string PreprocessForAndroidArm64(string text) {
        // Join backslash-escaped line continuations first
        string joined = text.Replace("\\\r\n", " ").Replace("\\\n", " ");
        StringBuilder output = new(joined.Length);
        Stack<PreprocFrame> stack = new();
        bool active = true;

        using StringReader sr = new(joined);
        string line;
        while ((line = sr.ReadLine()) != null) {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) {
                string directive = trimmed[1..].TrimStart();
                if (directive.StartsWith("ifdef ", StringComparison.Ordinal) || directive.StartsWith("ifdef\t", StringComparison.Ordinal)) {
                    string sym = ExtractFirstToken(directive[5..]);
                    bool cond = AndroidDefinedMacros.Contains(sym);
                    bool nextActive = active && cond;
                    stack.Push(new PreprocFrame { ParentActive = active, BranchActive = nextActive, BranchTaken = cond });
                    active = nextActive;
                    continue;
                }
                if (directive.StartsWith("ifndef ", StringComparison.Ordinal) || directive.StartsWith("ifndef\t", StringComparison.Ordinal)) {
                    string sym = ExtractFirstToken(directive[6..]);
                    bool cond = !AndroidDefinedMacros.Contains(sym);
                    bool nextActive = active && cond;
                    stack.Push(new PreprocFrame { ParentActive = active, BranchActive = nextActive, BranchTaken = cond });
                    active = nextActive;
                    continue;
                }
                if (directive.StartsWith("if ", StringComparison.Ordinal) || directive.StartsWith("if\t", StringComparison.Ordinal) || directive.StartsWith("if(", StringComparison.Ordinal)) {
                    string expr = directive[2..].Trim();
                    bool cond = EvaluatePreprocCondition(expr);
                    bool nextActive = active && cond;
                    stack.Push(new PreprocFrame { ParentActive = active, BranchActive = nextActive, BranchTaken = cond });
                    active = nextActive;
                    continue;
                }
                if (directive.StartsWith("elif ", StringComparison.Ordinal) || directive.StartsWith("elif\t", StringComparison.Ordinal) || directive.StartsWith("elif(", StringComparison.Ordinal)) {
                    if (stack.Count > 0) {
                        PreprocFrame top = stack.Pop();
                        bool cond = !top.BranchTaken && EvaluatePreprocCondition(directive[4..].Trim());
                        bool nextActive = top.ParentActive && cond;
                        top.BranchActive = nextActive;
                        top.BranchTaken = top.BranchTaken || cond;
                        stack.Push(top);
                        active = nextActive;
                    }
                    continue;
                }
                if (directive.StartsWith("else", StringComparison.Ordinal) &&
                    (directive.Length == 4 || !IsIdentChar(directive[4]))) {
                    if (stack.Count > 0) {
                        PreprocFrame top = stack.Pop();
                        bool nextActive = top.ParentActive && !top.BranchTaken;
                        top.BranchActive = nextActive;
                        top.BranchTaken = true;
                        stack.Push(top);
                        active = nextActive;
                    }
                    continue;
                }
                if (directive.StartsWith("endif", StringComparison.Ordinal) &&
                    (directive.Length == 5 || !IsIdentChar(directive[5]))) {
                    if (stack.Count > 0) {
                        PreprocFrame top = stack.Pop();
                        active = top.ParentActive;
                    }
                    continue;
                }
                // Preserve #define ORT_API_VERSION lines when active; drop other directives
                if (active && directive.StartsWith("define", StringComparison.Ordinal) && directive.Contains("ORT_API_VERSION", StringComparison.Ordinal)) {
                    output.AppendLine(trimmed);
                }
                continue;
            }

            if (active) {
                output.AppendLine(line);
            }
        }
        return output.ToString();
    }

    static string ExtractFirstToken(string s) {
        s = s.TrimStart();
        int i = 0;
        while (i < s.Length && IsIdentChar(s[i])) i++;
        return s.Substring(0, i);
    }

    static bool EvaluatePreprocCondition(string expr) {
        if (string.IsNullOrWhiteSpace(expr)) {
            return false;
        }
        // Replace defined(SYM) and defined SYM with 1 / 0
        string replaced = Regex.Replace(expr, @"\bdefined\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)",
            m => AndroidDefinedMacros.Contains(m.Groups[1].Value) ? "1" : "0");
        replaced = Regex.Replace(replaced, @"\bdefined\s+([A-Za-z_][A-Za-z0-9_]*)\b",
            m => AndroidDefinedMacros.Contains(m.Groups[1].Value) ? "1" : "0");
        // Replace remaining bare identifiers with 1 if defined on Android ARM64, else 0
        replaced = Regex.Replace(replaced, @"\b([A-Za-z_][A-Za-z0-9_]*)\b",
            m => AndroidDefinedMacros.Contains(m.Groups[1].Value) ? "1" : "0");

        int pos = 0;
        return ParseOr(replaced, ref pos) != 0;
    }

    static long ParseOr(string s, ref int pos) {
        long left = ParseAnd(s, ref pos);
        while (MatchToken(s, ref pos, "||")) {
            long right = ParseAnd(s, ref pos);
            left = (left != 0 || right != 0) ? 1 : 0;
        }
        return left;
    }

    static long ParseAnd(string s, ref int pos) {
        long left = ParseComparison(s, ref pos);
        while (MatchToken(s, ref pos, "&&")) {
            long right = ParseComparison(s, ref pos);
            left = (left != 0 && right != 0) ? 1 : 0;
        }
        return left;
    }

    static long ParseComparison(string s, ref int pos) {
        long left = ParseUnary(s, ref pos);
        if (MatchToken(s, ref pos, "==")) return left == ParseUnary(s, ref pos) ? 1 : 0;
        if (MatchToken(s, ref pos, "!=")) return left != ParseUnary(s, ref pos) ? 1 : 0;
        if (MatchToken(s, ref pos, ">=")) return left >= ParseUnary(s, ref pos) ? 1 : 0;
        if (MatchToken(s, ref pos, "<=")) return left <= ParseUnary(s, ref pos) ? 1 : 0;
        if (MatchToken(s, ref pos, ">")) return left > ParseUnary(s, ref pos) ? 1 : 0;
        if (MatchToken(s, ref pos, "<")) return left < ParseUnary(s, ref pos) ? 1 : 0;
        return left;
    }

    static long ParseUnary(string s, ref int pos) {
        SkipWs(s, ref pos);
        if (pos < s.Length && s[pos] == '!') {
            pos++;
            return ParseUnary(s, ref pos) == 0 ? 1 : 0;
        }
        if (pos < s.Length && s[pos] == '(') {
            pos++;
            long val = ParseOr(s, ref pos);
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ')') pos++;
            return val;
        }
        int start = pos;
        if (pos < s.Length && (s[pos] == '-' || s[pos] == '+')) pos++;
        while (pos < s.Length && char.IsDigit(s[pos])) pos++;
        if (pos > start && long.TryParse(s.Substring(start, pos - start), out long num)) {
            return num;
        }
        return 0;
    }

    static void SkipWs(string s, ref int pos) {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }

    static bool MatchToken(string s, ref int pos, string tok) {
        SkipWs(s, ref pos);
        if (pos + tok.Length <= s.Length && string.CompareOrdinal(s, pos, tok, 0, tok.Length) == 0) {
            // Avoid matching '>' when '>=' or '!' when '!='
            if (tok.Length == 1 && (tok[0] == '>' || tok[0] == '<') && pos + 1 < s.Length && s[pos + 1] == '=') {
                return false;
            }
            pos += tok.Length;
            return true;
        }
        return false;
    }
}
