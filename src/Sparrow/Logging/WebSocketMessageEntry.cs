using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Sparrow.Collections;

namespace Sparrow.Logging
{
    public class LogMessageEntry
    {
        public MemoryStream Data;
        public TaskCompletionSource<object> Task;

        public readonly List<WebSocket> WebSocketsList = new List<WebSocket>();

        public override string ToString()
        {
            if (Data == null)
                return null;

            return Encodings.Utf8.GetString(Data.ToArray());
        }
    }
}
