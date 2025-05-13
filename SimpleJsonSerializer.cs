using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace AaTurpin.SnapshotManager
{
    /// <summary>
    /// A simple JSON serializer/deserializer implementation without external dependencies
    /// </summary>
    public static class SimpleJsonSerializer
    {
        #region Serialization

        /// <summary>
        /// Serializes an object to a JSON string
        /// </summary>
        /// <param name="obj">The object to serialize</param>
        /// <param name="indented">Whether to use indentation for better readability</param>
        /// <returns>JSON string representation of the object</returns>
        public static string Serialize(object obj, bool indented = true)
        {
            if (obj == null)
                return "null";

            var sb = new StringBuilder();
            SerializeValue(obj, sb, indented ? 0 : -1);
            return sb.ToString();
        }

        private static void SerializeValue(object value, StringBuilder sb, int indent)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            var type = value.GetType();

            if (type == typeof(string))
            {
                SerializeString((string)value, sb);
            }
            else if (type == typeof(int) || type == typeof(long) || type == typeof(short) ||
                     type == typeof(byte) || type == typeof(sbyte) || type == typeof(uint) ||
                     type == typeof(ulong) || type == typeof(ushort))
            {
                sb.Append(value);
            }
            else if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            else if (type == typeof(bool))
            {
                sb.Append((bool)value ? "true" : "false");
            }
            else if (type == typeof(DateTime))
            {
                SerializeString(((DateTime)value).ToString("o"), sb);
            }
            else if (type == typeof(FileAttributes))
            {
                sb.Append((int)value);
            }
            else if (type.IsEnum)
            {
                SerializeString(value.ToString(), sb);
            }
            else if (value is IDictionary dict)
            {
                SerializeDictionary(dict, sb, indent);
            }
            else if (value is ICollection collection && !(value is string))
            {
                SerializeCollection(collection, sb, indent);
            }
            else
            {
                // For all other types, serialize as an object with properties
                SerializeObject(value, sb, indent);
            }
        }

        private static void SerializeString(string value, StringBuilder sb)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 32 || c > 127)
                        {
                            sb.Append("\\u" + ((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        private static void SerializeObject(object obj, StringBuilder sb, int indent)
        {
            Type type = obj.GetType();
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            sb.Append('{');

            if (indent >= 0)
                sb.AppendLine();

            bool first = true;
            foreach (var prop in properties)
            {
                // Skip indexers and properties without getters
                if (prop.GetIndexParameters().Length > 0 || !prop.CanRead)
                    continue;

                if (!first)
                {
                    sb.Append(',');
                    if (indent >= 0)
                        sb.AppendLine();
                }

                if (indent >= 0)
                    sb.Append(new string(' ', (indent + 1) * 2));

                SerializeString(prop.Name, sb);
                sb.Append(':');
                if (indent >= 0)
                    sb.Append(' ');

                try
                {
                    object value = prop.GetValue(obj);
                    SerializeValue(value, sb, indent >= 0 ? indent + 1 : -1);
                }
                catch (Exception)
                {
                    // If we can't get the property value, just output null
                    sb.Append("null");
                }

                first = false;
            }

            if (indent >= 0)
            {
                sb.AppendLine();
                sb.Append(new string(' ', indent * 2));
            }

            sb.Append('}');
        }

        private static void SerializeDictionary(IDictionary dict, StringBuilder sb, int indent)
        {
            sb.Append('{');

            if (indent >= 0)
                sb.AppendLine();

            bool first = true;
            foreach (DictionaryEntry entry in dict)
            {
                if (!first)
                {
                    sb.Append(',');
                    if (indent >= 0)
                        sb.AppendLine();
                }

                if (indent >= 0)
                    sb.Append(new string(' ', (indent + 1) * 2));

                // Dictionary keys must be strings in JSON
                if (entry.Key is string strKey)
                {
                    SerializeString(strKey, sb);
                }
                else
                {
                    SerializeString(entry.Key.ToString(), sb);
                }

                sb.Append(':');
                if (indent >= 0)
                    sb.Append(' ');

                SerializeValue(entry.Value, sb, indent >= 0 ? indent + 1 : -1);
                first = false;
            }

            if (indent >= 0)
            {
                sb.AppendLine();
                sb.Append(new string(' ', indent * 2));
            }

            sb.Append('}');
        }

        private static void SerializeCollection(ICollection collection, StringBuilder sb, int indent)
        {
            sb.Append('[');

            if (indent >= 0)
                sb.AppendLine();

            bool first = true;
            foreach (var item in collection)
            {
                if (!first)
                {
                    sb.Append(',');
                    if (indent >= 0)
                        sb.AppendLine();
                }

                if (indent >= 0)
                    sb.Append(new string(' ', (indent + 1) * 2));

                SerializeValue(item, sb, indent >= 0 ? indent + 1 : -1);
                first = false;
            }

            if (indent >= 0)
            {
                sb.AppendLine();
                sb.Append(new string(' ', indent * 2));
            }

            sb.Append(']');
        }

        #endregion

        #region Deserialization

        /// <summary>
        /// Deserializes a JSON string to an object of the specified type
        /// </summary>
        /// <typeparam name="T">The target type</typeparam>
        /// <param name="json">The JSON string to deserialize</param>
        /// <returns>The deserialized object</returns>
        public static T Deserialize<T>(string json) where T : new()
        {
            if (string.IsNullOrEmpty(json))
                return default;

            if (json == "null")
                return default;

            var jsonReader = new JsonReader(json);
            var value = jsonReader.ReadValue();

            return (T)ConvertJsonValueToType(value, typeof(T));
        }

        private static object ConvertJsonValueToType(object jsonValue, Type targetType)
        {
            if (jsonValue == null)
                return null;

            if (targetType.IsAssignableFrom(jsonValue.GetType()))
                return jsonValue;

            if (targetType == typeof(string))
            {
                return jsonValue.ToString();
            }
            else if (targetType == typeof(int))
            {
                return Convert.ToInt32(jsonValue);
            }
            else if (targetType == typeof(long))
            {
                return Convert.ToInt64(jsonValue);
            }
            else if (targetType == typeof(float))
            {
                return Convert.ToSingle(jsonValue);
            }
            else if (targetType == typeof(double))
            {
                return Convert.ToDouble(jsonValue);
            }
            else if (targetType == typeof(decimal))
            {
                return Convert.ToDecimal(jsonValue);
            }
            else if (targetType == typeof(bool))
            {
                return Convert.ToBoolean(jsonValue);
            }
            else if (targetType == typeof(DateTime))
            {
                if (jsonValue is string dateStr)
                    return DateTime.Parse(dateStr);
                return default(DateTime);
            }
            else if (targetType == typeof(FileAttributes))
            {
                if (jsonValue is double numValue)
                    return (FileAttributes)(int)numValue;
                else if (jsonValue is long longValue)
                    return (FileAttributes)longValue;
                return default(FileAttributes);
            }
            else if (targetType.IsEnum)
            {
                if (jsonValue is string enumStr)
                    return Enum.Parse(targetType, enumStr);
                return Enum.ToObject(targetType, Convert.ToInt32(jsonValue));
            }
            else if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                return DeserializeDictionary(jsonValue as Dictionary<string, object>, targetType);
            }
            else if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
            {
                return DeserializeList(jsonValue as List<object>, targetType);
            }
            else if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(HashSet<>))
            {
                return DeserializeHashSet(jsonValue as List<object>, targetType);
            }
            else
            {
                // Custom object deserialization
                return DeserializeObject(jsonValue as Dictionary<string, object>, targetType);
            }
        }

        private static object DeserializeObject(Dictionary<string, object> jsonObj, Type targetType)
        {
            if (jsonObj == null)
                return null;

            // Create an instance of the target type
            object instance = Activator.CreateInstance(targetType);

            // Get all properties of the target type
            var properties = targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                 .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
                                 .ToList();

            // Set each property from the JSON dictionary
            foreach (var prop in properties)
            {
                if (jsonObj.TryGetValue(prop.Name, out object jsonValue))
                {
                    try
                    {
                        object convertedValue = ConvertJsonValueToType(jsonValue, prop.PropertyType);
                        prop.SetValue(instance, convertedValue);
                    }
                    catch
                    {
                        // Skip properties that can't be set
                    }
                }
            }

            return instance;
        }

        private static object DeserializeDictionary(Dictionary<string, object> jsonDict, Type targetType)
        {
            if (jsonDict == null)
                return null;

            var keyType = targetType.GetGenericArguments()[0];
            var valueType = targetType.GetGenericArguments()[1];
            var dictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
            var dict = Activator.CreateInstance(dictType);
            var addMethod = dictType.GetMethod("Add", new[] { keyType, valueType });

            foreach (var entry in jsonDict)
            {
                var key = ConvertJsonValueToType(entry.Key, keyType);
                var value = ConvertJsonValueToType(entry.Value, valueType);
                addMethod.Invoke(dict, new[] { key, value });
            }

            return dict;
        }

        private static object DeserializeList(List<object> jsonList, Type targetType)
        {
            if (jsonList == null)
                return null;

            var itemType = targetType.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(itemType);
            var list = Activator.CreateInstance(listType);
            var addMethod = listType.GetMethod("Add");

            foreach (var item in jsonList)
            {
                var convertedItem = ConvertJsonValueToType(item, itemType);
                addMethod.Invoke(list, new[] { convertedItem });
            }

            return list;
        }

        private static object DeserializeHashSet(List<object> jsonList, Type targetType)
        {
            if (jsonList == null)
                return null;

            var itemType = targetType.GetGenericArguments()[0];
            var hashSetType = typeof(HashSet<>).MakeGenericType(itemType);
            var hashSet = Activator.CreateInstance(hashSetType);
            var addMethod = hashSetType.GetMethod("Add");

            foreach (var item in jsonList)
            {
                var convertedItem = ConvertJsonValueToType(item, itemType);
                addMethod.Invoke(hashSet, new[] { convertedItem });
            }

            return hashSet;
        }

        #endregion
    }
}
