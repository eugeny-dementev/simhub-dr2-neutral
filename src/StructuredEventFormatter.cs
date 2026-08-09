using System;
using System.Globalization;
using System.Text;

namespace DR2NeutralPlugin
{
    internal struct StructuredLogField
    {
        public StructuredLogField(string name, object value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; private set; }

        public object Value { get; private set; }
    }

    internal static class StructuredEventFormatter
    {
        internal const int MaximumEventNameLength = 80;
        internal const int MaximumFieldNameLength = 64;
        internal const int MaximumStringValueLength = 240;
        internal const int MaximumFieldCount = 24;

        public static StructuredLogField Field(string name, object value)
        {
            return new StructuredLogField(name, value);
        }

        public static string Format(
            DateTime timestampUtc,
            string eventName,
            params StructuredLogField[] fields)
        {
            string normalizedEventName = ValidateName(
                eventName,
                MaximumEventNameLength,
                "eventName");
            StructuredLogField[] normalizedFields = fields ?? new StructuredLogField[0];
            if (normalizedFields.Length > MaximumFieldCount)
            {
                throw new ArgumentOutOfRangeException(
                    "fields",
                    "Structured events support at most " + MaximumFieldCount + " fields.");
            }

            DateTime normalizedTimestamp = timestampUtc.Kind == DateTimeKind.Utc
                ? timestampUtc
                : timestampUtc.ToUniversalTime();

            StringBuilder json = new StringBuilder(256);
            json.Append("{\"timestampUtc\":\"");
            AppendEscapedJsonString(
                json,
                normalizedTimestamp.ToString("O", CultureInfo.InvariantCulture));
            json.Append("\",\"event\":\"");
            AppendEscapedJsonString(json, normalizedEventName);
            json.Append('"');

            for (int index = 0; index < normalizedFields.Length; index++)
            {
                StructuredLogField field = normalizedFields[index];
                string fieldName = ValidateName(
                    field.Name,
                    MaximumFieldNameLength,
                    "fields");

                json.Append(",\"");
                AppendEscapedJsonString(json, fieldName);
                json.Append("\":");
                AppendJsonValue(json, field.Value);
            }

            json.Append('}');
            return json.ToString();
        }

        private static string ValidateName(
            string value,
            int maximumLength,
            string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Structured log names cannot be empty.", parameterName);
            }

            string normalized = value.Trim();
            if (normalized.Length > maximumLength)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "Structured log name exceeds " + maximumLength + " characters.");
            }

            for (int index = 0; index < normalized.Length; index++)
            {
                char character = normalized[index];
                bool allowed = char.IsLetterOrDigit(character) ||
                               character == '.' ||
                               character == '_' ||
                               character == '-';
                if (!allowed)
                {
                    throw new ArgumentException(
                        "Structured log names may contain only letters, digits, '.', '_', and '-'.",
                        parameterName);
                }
            }

            return normalized;
        }

        private static void AppendJsonValue(StringBuilder json, object value)
        {
            if (value == null)
            {
                json.Append("null");
                return;
            }

            if (value is bool)
            {
                json.Append((bool)value ? "true" : "false");
                return;
            }

            if (value is string || value is char || value is Guid || value is Enum)
            {
                AppendQuotedString(json, Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            if (value is DateTime)
            {
                DateTime timestamp = (DateTime)value;
                AppendQuotedString(
                    json,
                    timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                return;
            }

            if (value is float)
            {
                float number = (float)value;
                if (float.IsNaN(number) || float.IsInfinity(number))
                {
                    json.Append("null");
                    return;
                }
            }

            if (value is double)
            {
                double number = (double)value;
                if (double.IsNaN(number) || double.IsInfinity(number))
                {
                    json.Append("null");
                    return;
                }
            }

            IFormattable formattable = value as IFormattable;
            if (formattable != null && IsJsonNumber(value))
            {
                json.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            }

            AppendQuotedString(json, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static bool IsJsonNumber(object value)
        {
            return value is byte ||
                   value is sbyte ||
                   value is short ||
                   value is ushort ||
                   value is int ||
                   value is uint ||
                   value is long ||
                   value is ulong ||
                   value is float ||
                   value is double ||
                   value is decimal;
        }

        private static void AppendQuotedString(StringBuilder json, string value)
        {
            string bounded = value ?? "";
            if (bounded.Length > MaximumStringValueLength)
            {
                bounded = bounded.Substring(0, MaximumStringValueLength);
            }

            json.Append('"');
            AppendEscapedJsonString(json, bounded);
            json.Append('"');
        }

        private static void AppendEscapedJsonString(StringBuilder json, string value)
        {
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"':
                        json.Append("\\\"");
                        break;
                    case '\\':
                        json.Append("\\\\");
                        break;
                    case '\b':
                        json.Append("\\b");
                        break;
                    case '\f':
                        json.Append("\\f");
                        break;
                    case '\n':
                        json.Append("\\n");
                        break;
                    case '\r':
                        json.Append("\\r");
                        break;
                    case '\t':
                        json.Append("\\t");
                        break;
                    default:
                        if (character < 0x20)
                        {
                            json.Append("\\u");
                            json.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            json.Append(character);
                        }
                        break;
                }
            }
        }
    }
}
