using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ILauncher.Bootstrapper
{
    internal sealed class JsonNumber
    {
        internal JsonNumber(string text) { Text = text; }
        internal string Text { get; private set; }
    }

    internal sealed class StrictJson
    {
        private readonly string _text;
        private int _index;

        private StrictJson(string text) { _text = text; }

        internal static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            StrictJson parser = new StrictJson(text);
            object value = parser.ParseValue(0);
            parser.SkipWhiteSpace();
            if (parser._index != text.Length) throw new FormatException("Unexpected data after JSON value.");
            return value;
        }

        private object ParseValue(int depth)
        {
            if (depth > 32) throw new FormatException("JSON nesting limit exceeded.");
            SkipWhiteSpace();
            if (_index >= _text.Length) throw new FormatException("Unexpected end of JSON.");
            char current = _text[_index];
            if (current == '{') return ParseObject(depth + 1);
            if (current == '[') return ParseArray(depth + 1);
            if (current == '"') return ParseString();
            if (current == 't') { ConsumeKeyword("true"); return true; }
            if (current == 'f') { ConsumeKeyword("false"); return false; }
            if (current == 'n') { ConsumeKeyword("null"); return null; }
            if (current == '-' || (current >= '0' && current <= '9')) return ParseNumber();
            throw new FormatException("Invalid JSON value.");
        }

        private Dictionary<string, object> ParseObject(int depth)
        {
            _index++;
            SkipWhiteSpace();
            Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
            if (ConsumeIf('}')) return values;
            while (true)
            {
                SkipWhiteSpace();
                if (_index >= _text.Length || _text[_index] != '"')
                    throw new FormatException("A JSON object key must be a string.");
                string key = ParseString();
                if (values.ContainsKey(key)) throw new FormatException("Duplicate JSON object key.");
                SkipWhiteSpace();
                Require(':');
                object value = ParseValue(depth);
                values.Add(key, value);
                SkipWhiteSpace();
                if (ConsumeIf('}')) return values;
                Require(',');
            }
        }

        private List<object> ParseArray(int depth)
        {
            _index++;
            SkipWhiteSpace();
            List<object> values = new List<object>();
            if (ConsumeIf(']')) return values;
            while (true)
            {
                values.Add(ParseValue(depth));
                SkipWhiteSpace();
                if (ConsumeIf(']')) return values;
                Require(',');
            }
        }

        private string ParseString()
        {
            Require('"');
            StringBuilder builder = new StringBuilder();
            while (_index < _text.Length)
            {
                char current = _text[_index++];
                if (current == '"') return builder.ToString();
                if (current < 0x20) throw new FormatException("Unescaped control character in JSON string.");
                if (current != '\\') { builder.Append(current); continue; }
                if (_index >= _text.Length) throw new FormatException("Incomplete JSON escape.");
                char escape = _text[_index++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u': builder.Append(ParseUnicodeEscape()); break;
                    default: throw new FormatException("Invalid JSON escape.");
                }
            }
            throw new FormatException("Unterminated JSON string.");
        }

        private char ParseUnicodeEscape()
        {
            if (_index + 4 > _text.Length) throw new FormatException("Incomplete Unicode escape.");
            int value = 0;
            for (int i = 0; i < 4; i++)
            {
                char digit = _text[_index++];
                int nibble;
                if (digit >= '0' && digit <= '9') nibble = digit - '0';
                else if (digit >= 'a' && digit <= 'f') nibble = digit - 'a' + 10;
                else if (digit >= 'A' && digit <= 'F') nibble = digit - 'A' + 10;
                else throw new FormatException("Invalid Unicode escape.");
                value = (value << 4) | nibble;
            }
            return (char)value;
        }

        private JsonNumber ParseNumber()
        {
            int start = _index;
            if (ConsumeIf('-') && _index >= _text.Length) throw new FormatException("Invalid JSON number.");
            if (ConsumeIf('0'))
            {
                if (_index < _text.Length && IsDigit(_text[_index])) throw new FormatException("Leading zero in JSON number.");
            }
            else
            {
                if (_index >= _text.Length || _text[_index] < '1' || _text[_index] > '9')
                    throw new FormatException("Invalid JSON number.");
                while (_index < _text.Length && IsDigit(_text[_index])) _index++;
            }
            if (ConsumeIf('.'))
            {
                if (_index >= _text.Length || !IsDigit(_text[_index])) throw new FormatException("Invalid JSON fraction.");
                while (_index < _text.Length && IsDigit(_text[_index])) _index++;
            }
            if (_index < _text.Length && (_text[_index] == 'e' || _text[_index] == 'E'))
            {
                _index++;
                if (_index < _text.Length && (_text[_index] == '+' || _text[_index] == '-')) _index++;
                if (_index >= _text.Length || !IsDigit(_text[_index])) throw new FormatException("Invalid JSON exponent.");
                while (_index < _text.Length && IsDigit(_text[_index])) _index++;
            }
            return new JsonNumber(_text.Substring(start, _index - start));
        }

        private void ConsumeKeyword(string keyword)
        {
            if (_index + keyword.Length > _text.Length ||
                String.CompareOrdinal(_text, _index, keyword, 0, keyword.Length) != 0)
                throw new FormatException("Invalid JSON token.");
            _index += keyword.Length;
        }

        private void SkipWhiteSpace()
        {
            while (_index < _text.Length)
            {
                char c = _text[_index];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _index++;
                else break;
            }
        }

        private bool ConsumeIf(char expected)
        {
            if (_index < _text.Length && _text[_index] == expected) { _index++; return true; }
            return false;
        }

        private void Require(char expected)
        {
            if (!ConsumeIf(expected)) throw new FormatException("Malformed JSON structure.");
        }

        private static bool IsDigit(char c) { return c >= '0' && c <= '9'; }
    }

    internal static class CatalogParser
    {
        internal static BootstrapperCatalog Parse(string json, string catalogHash,
            BootstrapperOptions options)
        {
            object parsed = StrictJson.Parse(json);
            Dictionary<string, object> root = Object(parsed, "catalog");
            RequireKeys(root, new[] { "schemaVersion", "version", "source", "runtime", "apps" });
            int schema = Int32(root["schemaVersion"], "schemaVersion");
            if (schema != 1) throw new FormatException("Unsupported online catalog schema.");
            string version = Text(root["version"], "version", 64, false);
            if (!IsSafeToken(version) || version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Invalid release version in catalog.");

            Dictionary<string, object> sourceObject = Object(root["source"], "source");
            RequireKeys(sourceObject, new[] { "serverUrl", "apiUrl", "repository", "tagName" });
            string serverUrl = Text(sourceObject["serverUrl"], "source.serverUrl", 512, false);
            string apiUrl = Text(sourceObject["apiUrl"], "source.apiUrl", 512, false);
            string repository = Text(sourceObject["repository"], "source.repository", 256, false);
            string tagName = Text(sourceObject["tagName"], "source.tagName", 64, false);
            TrustedHttpsUri(serverUrl, "source.serverUrl");
            TrustedHttpsUri(apiUrl, "source.apiUrl");
            if (!RegexMatch(repository, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
                throw new FormatException("Invalid repository in catalog.");
            if (!IsSafeToken(tagName) || !String.Equals(tagName.TrimStart('v', 'V'), version,
                    StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Catalog release tag does not match its version.");
            Dictionary<string, object> runtimeObject = Object(root["runtime"], "runtime");
            RequireKeys(runtimeObject, new[] { "version", "asset", "sha256", "bytes" });
            string runtimeVersion = Text(runtimeObject["version"], "runtime.version", 64, false);
            if (!RegexMatch(runtimeVersion, "^[0-9]+\\.[0-9]+\\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?$"))
                throw new FormatException("Invalid runtime version.");
            BootstrapperRuntime runtime = new BootstrapperRuntime(runtimeVersion,
                AssetName(runtimeObject["asset"], "runtime.asset"),
                Hash(runtimeObject["sha256"], "runtime.sha256"),
                PositiveBytes(runtimeObject["bytes"], "runtime.bytes", options));

            List<object> appObjects = Array(root["apps"], "apps");
            if (appObjects.Count == 0 || appObjects.Count > 200)
                throw new FormatException("The catalog must contain between one and 200 apps.");
            List<BootstrapperApp> apps = new List<BootstrapperApp>();
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            assets.Add(runtime.Asset);
            foreach (object item in appObjects)
            {
                Dictionary<string, object> appObject = Object(item, "app");
                RequireKeys(appObject, new[] { "id", "name", "description", "category", "executable", "asset", "sha256", "bytes" },
                    new[] { "iconBase64" });
                string id = Text(appObject["id"], "app.id", 80, false);
                if (!RegexMatch(id, "^[a-z0-9][a-z0-9._-]{0,79}$") || id.EndsWith(".", StringComparison.Ordinal) ||
                    IsReservedDeviceName(id) || !ids.Add(id))
                    throw new FormatException("Invalid or duplicate app ID.");
                string name = Text(appObject["name"], "app.name", 128, false);
                string description = Text(appObject["description"], "app.description", 2048, true);
                string category = Text(appObject["category"], "app.category", 80, false);
                string executable = RelativeExecutable(Text(appObject["executable"], "app.executable", 240, false));
                string icon = appObject.ContainsKey("iconBase64") && appObject["iconBase64"] != null
                    ? Icon(appObject["iconBase64"]) : null;
                string asset = AssetName(appObject["asset"], "app.asset");
                if (!assets.Add(asset)) throw new FormatException("Duplicate package asset name.");
                apps.Add(new BootstrapperApp(id, name, description, category, executable, icon, asset,
                    Hash(appObject["sha256"], "app.sha256"),
                    PositiveBytes(appObject["bytes"], "app.bytes", options)));
            }
            return new BootstrapperCatalog(schema, version,
                new BootstrapperSource(serverUrl, apiUrl, repository, tagName), runtime, apps, catalogHash);
        }

        internal static Dictionary<string, object> Object(object value, string name)
        {
            Dictionary<string, object> result = value as Dictionary<string, object>;
            if (result == null) throw new FormatException(name + " must be a JSON object.");
            return result;
        }

        private static List<object> Array(object value, string name)
        {
            List<object> result = value as List<object>;
            if (result == null) throw new FormatException(name + " must be a JSON array.");
            return result;
        }

        private static string Text(object value, string name, int maxLength, bool allowEmpty)
        {
            string result = value as string;
            if (result == null || result.Length > maxLength || (!allowEmpty && String.IsNullOrWhiteSpace(result)))
                throw new FormatException(name + " is invalid.");
            return result;
        }

        private static int Int32(object value, string name)
        {
            long result = Integer(value, name);
            if (result < System.Int32.MinValue || result > System.Int32.MaxValue) throw new FormatException(name + " is out of range.");
            return (int)result;
        }

        private static long PositiveBytes(object value, string name, BootstrapperOptions options)
        {
            long bytes = Integer(value, name);
            long maximum = options == null ? 1024L * 1024L * 1024L : options.MaximumPackageBytes;
            if (bytes <= 0 || bytes > maximum) throw new FormatException(name + " is outside the allowed range.");
            return bytes;
        }

        private static long Integer(object value, string name)
        {
            JsonNumber number = value as JsonNumber;
            long parsed;
            if (number == null || number.Text.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0 ||
                !Int64.TryParse(number.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out parsed))
                throw new FormatException(name + " must be an integer.");
            return parsed;
        }

        private static string Hash(object value, string name)
        {
            string hash = Text(value, name, 64, false);
            if (!RegexMatch(hash, "^[A-Fa-f0-9]{64}$")) throw new FormatException(name + " must be a SHA-256 hex digest.");
            return hash.ToLowerInvariant();
        }

        private static string AssetName(object value, string name)
        {
            string asset = Text(value, name, 128, false);
            if (asset == "." || asset == ".." || asset.IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0 ||
                !RegexMatch(asset, "^[A-Za-z0-9][A-Za-z0-9._-]*\\.zip$"))
                throw new FormatException(name + " must be a safe ZIP file name.");
            return asset;
        }

        internal static string RelativeExecutable(string value)
        {
            string path = value.Replace('\\', '/');
            if (path.StartsWith("/", StringComparison.Ordinal) || path.IndexOf(':') >= 0 ||
                path.IndexOf('\0') >= 0 || path.Length > 240 || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("App executable must be a safe relative .exe path.");
            string[] parts = path.Split('/');
            foreach (string part in parts)
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) ||
                    part.EndsWith(" ", StringComparison.Ordinal) || IsReservedDeviceName(part))
                    throw new FormatException("App executable contains an unsafe path segment.");
            return String.Join(Path.DirectorySeparatorChar.ToString(), parts);
        }

        private static string Icon(object value)
        {
            string base64 = Text(value, "app.iconBase64", 2 * 1024 * 1024, false);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64); }
            catch (FormatException) { throw new FormatException("app.iconBase64 is not valid base64."); }
            if (bytes.Length > 1024 * 1024) throw new FormatException("App icon exceeds the size limit.");
            return base64;
        }

        private static Uri TrustedHttpsUri(string value, string name)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Query) ||
                !String.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/" && name.EndsWith("serverUrl", StringComparison.Ordinal))
                throw new FormatException(name + " must be an HTTPS origin.");
            return uri;
        }

        private static bool IsSafeToken(string value)
        {
            return RegexMatch(value, "^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$");
        }

        private static bool RegexMatch(string value, string pattern)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(value, pattern,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        private static bool IsReservedDeviceName(string value)
        {
            string stem = value.Split('.')[0];
            return new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
                .Contains(stem, StringComparer.OrdinalIgnoreCase);
        }

        private static void RequireKeys(Dictionary<string, object> value, string[] required, string[] optional = null)
        {
            HashSet<string> allowed = new HashSet<string>(required, StringComparer.Ordinal);
            if (optional != null) foreach (string key in optional) allowed.Add(key);
            foreach (string key in required)
                if (!value.ContainsKey(key)) throw new FormatException("Missing required catalog field: " + key);
            foreach (string key in value.Keys)
                if (!allowed.Contains(key)) throw new FormatException("Unsupported catalog field: " + key);
        }
    }
}
