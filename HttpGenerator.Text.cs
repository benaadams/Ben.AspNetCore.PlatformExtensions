using System;
using System.Collections.Generic;
using System.Text;

namespace PlatformExtensions
{
    public partial class HttpGenerator
    {
        private const string _crlf = "\r\n";
        private const string _eoh = "\r\n\r\n"; // End Of Headers
        private const string _http11OK = "HTTP/1.1 200 OK\r\n";
        private const string _http11NotFound = "HTTP/1.1 404 Not Found\r\n";
        private const string _headerServer = "Server: K";
        private const string _headerContentLength = "Content-Length: ";
        private const string _headerContentLengthZero = "Content-Length: 0";
        private const string _headerContentTypeText = "Content-Type: text/plain";
        private const string _headerContentTypeJson = "Content-Type: application/json";
        private const string _headerContentTypeHtml = "Content-Type: text/html; charset=UTF-8";

        private const string utilitiesText = @"
using System;
using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PlatformExtensions
{
    sealed class Utilities
    {
        [ThreadStatic]
        private static Utf8JsonWriter t_jsonWriter;

        public static JsonSerializerOptions SerializerOptions { get; } = new JsonSerializerOptions();

        public static Utf8JsonWriter GetJsonWriter(IBufferWriter<byte> writer)
        {
            Utf8JsonWriter utf8JsonWriter = t_jsonWriter;
            if (utf8JsonWriter is not null)
            {
                utf8JsonWriter.Reset(writer);
                return utf8JsonWriter;
            }

            return CreateJsonWriter(writer);

            static Utf8JsonWriter CreateJsonWriter(IBufferWriter<byte> writer)
            {
                Utf8JsonWriter utf8JsonWriter = new Utf8JsonWriter(writer, new JsonWriterOptions { SkipValidation = true });
                t_jsonWriter = utf8JsonWriter;
                return utf8JsonWriter;
            }
        }

        const int prefixLength = 8; // ""\r\nDate: "".Length
        const int dateTimeRLength = 29; // Wed, 14 Mar 2018 14:20:00 GMT
        const int suffixLength = 2; // crlf
        const int suffixIndex = dateTimeRLength + prefixLength;

        private static readonly Timer s_timer = new Timer((s) => {
            SetDateValues(DateTimeOffset.UtcNow);
        }, null, 1000, 1000);

        private static byte[] s_headerBytesMaster = new byte[prefixLength + dateTimeRLength + 2 * suffixLength];
        private static byte[] s_headerBytesScratch = new byte[prefixLength + dateTimeRLength + 2 * suffixLength];

        static Utilities()
        {
            var utf8 = Encoding.ASCII.GetBytes(""\r\nDate: "").AsSpan();

            utf8.CopyTo(s_headerBytesMaster);
            utf8.CopyTo(s_headerBytesScratch);
            s_headerBytesMaster[suffixIndex] = (byte)'\r';
            s_headerBytesMaster[suffixIndex + 1] = (byte)'\n';
            s_headerBytesMaster[suffixIndex + 2] = (byte)'\r';
            s_headerBytesMaster[suffixIndex + 3] = (byte)'\n';
            s_headerBytesScratch[suffixIndex] = (byte)'\r';
            s_headerBytesScratch[suffixIndex + 1] = (byte)'\n';
            s_headerBytesScratch[suffixIndex + 2] = (byte)'\r';
            s_headerBytesScratch[suffixIndex + 3] = (byte)'\n';

            SetDateValues(DateTimeOffset.UtcNow);
            SyncDateTimer();
        }

        private static void SyncDateTimer() => s_timer.Change(1000, 1000);

        public static ReadOnlySpan<byte> DateHeaderBytes => s_headerBytesMaster;

        private static void SetDateValues(DateTimeOffset value)
        {
            lock (s_headerBytesScratch)
            {
                if (!Utf8Formatter.TryFormat(value, s_headerBytesScratch.AsSpan(prefixLength), out var written, 'R'))
                {
                    throw new Exception(""date time format failed"");
                }
                Debug.Assert(written == dateTimeRLength);
                var temp = s_headerBytesMaster;
                s_headerBytesMaster = s_headerBytesScratch;
                s_headerBytesScratch = temp;
            }
        }

    }
}
";

        private const string routeAttributeText = @"
using System;
namespace PlatformExtensions
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    sealed class RouteAttribute : Attribute
    {
        public RouteAttribute(string path)
        {
            Path = path;
            ContentType = ""text/plain"";
        }

        public string Path { get; }
        public string ContentType { get; set; }
    }
}
";
        private const string mustacheViewAttributeText = @"
using System;
namespace PlatformExtensions
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    sealed class MustacheViewAttribute : Attribute
    {
        public MustacheViewAttribute(string path)
        {
            ViewPath = path;
        }

        public string ViewPath { get; set; }
    }
}
";
    }
}
