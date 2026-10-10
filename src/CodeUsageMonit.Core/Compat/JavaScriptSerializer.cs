// Compatibility layer for System.Web.Script.Serialization (removed after .NET Framework).
// J.Serializer(), Store.Write/Read and the self-tests all go through this surface,
// re-implemented on System.Text.Json so every call site keeps its original shape:
//   · Serialize(obj) walks public fields AND public properties (getters suffice,
//     read-only properties included) — exactly what JavaScriptSerializer serialized.
//   · [ScriptIgnore] hides members from serialization.
//   · Deserialize<T> sets public fields and public properties with setters,
//     matching member names case-insensitively.
//   · DeserializeObject returns the Dictionary<string, object> / object[] / primitive
//     shape that J.Parse (and thirty callers) depend on. JSON integers box as
//     int, then long; everything else as double (closest to the original boxing).
//   · Output is compact JSON with raw non-ASCII text (中文 emitted verbatim;
//     self-tests assert on `\"label\":\"当前账期\"` substrings).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace System.Web.Script.Serialization {
    // Same name and effect as the framework attribute: skip this member when serializing.
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method)]
    public sealed class ScriptIgnoreAttribute : Attribute {
        public ScriptIgnoreAttribute() { }
        public bool ApplyToOverrides { get; set; }
    }

    public sealed class JavaScriptSerializer {
        public int MaxJsonLength { get; set; } = 2097152;
        public int RecursionLimit { get; set; } = 100;

        // One shared options object; the contract modifiers give the JavaScriptSerializer
        // member semantics (fields + read-only properties in, ScriptIgnore out).
        private static readonly JsonSerializerOptions Options = CreateOptions();
        // typed 反序列化按 RecursionLimit 限深；默认 100 的那份静态复用（每次克隆会丢掉
        // options 缓存），非默认值按次克隆（内部调用只经 J.Serializer() 的默认值）。
        private static readonly JsonSerializerOptions DefaultTypedOptions = Typed(100);
        private static JsonSerializerOptions Typed(int recursionLimit) {
            var options = new JsonSerializerOptions(Options);
            options.MaxDepth = Math.Max(1, recursionLimit);
            return options;
        }
        private JsonSerializerOptions TypedOptions() {
            int depth = Math.Max(1, RecursionLimit);
            return depth == 100 ? DefaultTypedOptions : Typed(depth);
        }
        private static JsonSerializerOptions CreateOptions() {
            var options = new JsonSerializerOptions {
                IncludeFields = true,
                PropertyNameCaseInsensitive = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                NumberHandling = JsonNumberHandling.Strict
            };
            options.TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { DropScriptIgnore, AddReadOnlyProperties } };
            return options;
        }

        private static void DropScriptIgnore(JsonTypeInfo type) {
            if (type.Kind != JsonTypeInfoKind.Object) return;
            for (int i = type.Properties.Count - 1; i >= 0; i--) {
                if (type.Properties[i].AttributeProvider is MemberInfo member && member.IsDefined(typeof(ScriptIgnoreAttribute), false)) type.Properties.RemoveAt(i);
            }
        }

        // System.Text.Json omits get-only properties in some versions; JavaScriptSerializer
        // always serialized them (e.g. Quota.Remaining, ProductConsumption.DisplayName without
        // the ignore). Add any missing public readable instance properties with Set = null,
        // which also keeps them out of deserialization — like the original.
        private static void AddReadOnlyProperties(JsonTypeInfo type) {
            if (type.Kind != JsonTypeInfoKind.Object) return;
            Type t = type.Type;
            if (t.IsPrimitive || t == typeof(string) || t == typeof(decimal) || t == typeof(object) || typeof(System.Collections.IEnumerable).IsAssignableFrom(t)) return;
            foreach (PropertyInfo property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                if (property.GetMethod == null || !property.GetMethod.IsPublic || property.GetIndexParameters().Length > 0) continue;
                if (property.IsDefined(typeof(ScriptIgnoreAttribute), false)) continue;
                if (type.Properties.Any(p => string.Equals(p.Name, property.Name, StringComparison.Ordinal))) continue;
                JsonPropertyInfo added;
                try { added = type.CreateJsonPropertyInfo(property.PropertyType, property.Name); } catch { continue; }
                added.AttributeProvider = property;
                try { added.Get = obj => property.GetValue(obj); } catch { continue; }
                added.Set = null;
                type.Properties.Add(added);
            }
        }

        // Runtime type (anonymous types, Dictionary<string, object>, …), like SerializeObject.
        // 声明的 MaxJsonLength 同样约束输出：超长拒绝（与 .NET Framework 的保护语义对齐）。
        public string Serialize(object obj) {
            string json = JsonSerializer.Serialize(obj, obj == null ? typeof(object) : obj.GetType(), Options);
            if (json.Length > MaxJsonLength) throw new ArgumentException(JsonLengthMessage);
            return json;
        }
        public void Serialize(object obj, StringBuilder output) { output.Append(Serialize(obj)); }

        private const string JsonLengthMessage = "The length of the JSON string exceeds the value set on the MaxJsonLength property.";

        public T Deserialize<T>(string input) {
            string text = input ?? "";
            if (text.Length > MaxJsonLength) throw new ArgumentException(JsonLengthMessage);
            try { return JsonSerializer.Deserialize<T>(text, TypedOptions()); }
            catch (JsonException e) { throw new ArgumentException("Invalid JSON: " + e.Message, e); }
        }

        // JSON → Dictionary<string, object> / object[] / string / bool / boxed number / null.
        // MaxJsonLength 与 RecursionLimit 两条路径（typed / object）都生效：输入长度与
        // 嵌套深度超限都拒绝。深度按 RecursionLimit 限（MaxDepth 计数含根、上限即本值：
        // RecursionLimit=2 接受 [[1]]、拒绝 [[[]]]三层——与框架语义一致；旧实现这里固定
        // 128，typed 路径是 System.Text.Json 默认 64，调用者设置的保护边界两者都不成立）。
        // 解析失败与 typed 路径一样包装成 ArgumentException（框架的 JavaScriptSerializer
        // 没有 JsonException 这个类型；代码库无任何调用方依赖原始类型）。
        public object DeserializeObject(string input) {
            if (String.IsNullOrWhiteSpace(input)) throw new ArgumentException("Invalid JSON: empty input");
            if (input.Length > MaxJsonLength) throw new ArgumentException(JsonLengthMessage);
            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = Math.Max(1, RecursionLimit) };
            try {
                using (JsonDocument document = JsonDocument.Parse(input, options)) return FromElement(document.RootElement);
            } catch (JsonException e) { throw new ArgumentException("Invalid JSON: " + e.Message, e); }
        }

        private static object FromElement(JsonElement element) {
            switch (element.ValueKind) {
                case JsonValueKind.Object: {
                    var result = new Dictionary<string, object>();
                    foreach (JsonProperty property in element.EnumerateObject()) result[property.Name] = FromElement(property.Value);
                    return result;
                }
                case JsonValueKind.Array: {
                    var items = new List<object>();
                    foreach (JsonElement item in element.EnumerateArray()) items.Add(FromElement(item));
                    return items.ToArray();
                }
                case JsonValueKind.String: return element.GetString();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Number: return Number(element);
                default: return null;
            }
        }

        private static object Number(JsonElement element) {
            string raw = element.GetRawText();
            if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0) {
                if (element.TryGetInt32(out int i)) return i;
                if (element.TryGetInt64(out long l)) return l;
            }
            return element.GetDouble();
        }
    }
}
