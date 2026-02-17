using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VAMLaunchPlugin
{
    /// <summary>
    /// Minimal WebSocket client over TcpClient.
    /// Compatible with Unity/Mono runtime used by VaM.
    /// Only supports text frames (sufficient for Buttplug protocol).
    /// </summary>
    public class SimpleWebSocket
    {
        private TcpClient _tcp;
        private NetworkStream _stream;
        private volatile bool _connected;
        private Thread _recvThread;
        private readonly Queue<string> _recvQueue = new Queue<string>();
        private readonly Queue<string> _errorQueue = new Queue<string>();
        private readonly Random _random = new Random();
        private readonly object _sendLock = new object();

        public bool IsConnected
        {
            get { return _connected; }
        }

        public int QueuedMessageCount
        {
            get { lock (_recvQueue) { return _recvQueue.Count; } }
        }

        public bool Connect(string host, int port, string path = "/")
        {
            try
            {
                _tcp = new TcpClient();
                _tcp.NoDelay = true;
                _tcp.ReceiveTimeout = 0;
                _tcp.SendTimeout = 5000;
                _tcp.Connect(host, port);
                _stream = _tcp.GetStream();

                // Build WebSocket upgrade request
                string wsKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
                string request =
                    "GET " + path + " HTTP/1.1\r\n" +
                    "Host: " + host + ":" + port + "\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Sec-WebSocket-Key: " + wsKey + "\r\n" +
                    "Sec-WebSocket-Version: 13\r\n" +
                    "\r\n";

                byte[] reqBytes = Encoding.UTF8.GetBytes(request);
                _stream.Write(reqBytes, 0, reqBytes.Length);

                // Read HTTP upgrade response
                byte[] buf = new byte[4096];
                int bytesRead = _stream.Read(buf, 0, buf.Length);
                string response = Encoding.UTF8.GetString(buf, 0, bytesRead);

                if (!response.Contains("101"))
                {
                    _tcp.Close();
                    EnqueueError("WebSocket upgrade failed: " +
                                 response.Substring(0, Math.Min(120, response.Length)));
                    return false;
                }

                _connected = true;

                _recvThread = new Thread(ReceiveLoop);
                _recvThread.IsBackground = true;
                _recvThread.Start();

                return true;
            }
            catch (Exception e)
            {
                EnqueueError("WebSocket connect error: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Send a text message through the WebSocket. Thread-safe.
        /// </summary>
        public void Send(string message)
        {
            if (!_connected) return;

            byte[] payload = Encoding.UTF8.GetBytes(message);
            byte[] frame = BuildTextFrame(payload);

            lock (_sendLock)
            {
                try
                {
                    _stream.Write(frame, 0, frame.Length);
                    _stream.Flush();
                }
                catch (Exception e)
                {
                    _connected = false;
                    EnqueueError("WebSocket send error: " + e.Message);
                }
            }
        }

        /// <summary>
        /// Dequeue the next received text message, or null if empty.
        /// Call from main thread.
        /// </summary>
        public string GetNextMessage()
        {
            lock (_recvQueue)
            {
                return _recvQueue.Count > 0 ? _recvQueue.Dequeue() : null;
            }
        }

        /// <summary>
        /// Dequeue the next error string, or null if none.
        /// Call from main thread.
        /// </summary>
        public string GetNextError()
        {
            lock (_errorQueue)
            {
                return _errorQueue.Count > 0 ? _errorQueue.Dequeue() : null;
            }
        }

        public void Close()
        {
            _connected = false;

            // Attempt to send a close frame
            try
            {
                if (_stream != null && _tcp != null && _tcp.Connected)
                {
                    byte[] mask = new byte[4];
                    _random.NextBytes(mask);
                    byte[] closeFrame = new byte[] { 0x88, 0x80, mask[0], mask[1], mask[2], mask[3] };
                    _stream.Write(closeFrame, 0, closeFrame.Length);
                }
            }
            catch { }

            try { _stream.Close(); } catch { }
            try { _tcp.Close(); } catch { }

            if (_recvThread != null && _recvThread.IsAlive)
            {
                _recvThread.Abort();
            }
        }

        // ----------------------------------------------------------------
        //  Private
        // ----------------------------------------------------------------

        private void ReceiveLoop()
        {
            try
            {
                while (_connected)
                {
                    string msg = ReadTextFrame();
                    if (msg != null)
                    {
                        lock (_recvQueue)
                        {
                            _recvQueue.Enqueue(msg);
                        }
                    }
                }
            }
            catch (ThreadAbortException) { }
            catch (Exception e)
            {
                if (_connected)
                {
                    _connected = false;
                    EnqueueError("WebSocket receive error: " + e.Message);
                }
            }
        }

        private string ReadTextFrame()
        {
            // Read 2-byte header
            byte[] header = ReadExact(2);
            if (header == null) return null;

            int opcode = header[0] & 0x0F;
            bool masked = (header[1] & 0x80) != 0;
            long payloadLen = header[1] & 0x7F;

            if (payloadLen == 126)
            {
                byte[] ext = ReadExact(2);
                if (ext == null) return null;
                payloadLen = (ext[0] << 8) | ext[1];
            }
            else if (payloadLen == 127)
            {
                byte[] ext = ReadExact(8);
                if (ext == null) return null;
                payloadLen = 0;
                for (int i = 0; i < 8; i++)
                    payloadLen = (payloadLen << 8) | ext[i];
            }

            byte[] maskKey = null;
            if (masked)
            {
                maskKey = ReadExact(4);
                if (maskKey == null) return null;
            }

            byte[] payload = ReadExact((int)payloadLen);
            if (payload == null) return null;

            if (masked && maskKey != null)
            {
                for (int i = 0; i < payload.Length; i++)
                    payload[i] ^= maskKey[i % 4];
            }

            switch (opcode)
            {
                case 0x01: // Text
                    return Encoding.UTF8.GetString(payload);

                case 0x02: // Binary (ignore, Buttplug uses text only)
                    return null;

                case 0x08: // Close
                    _connected = false;
                    return null;

                case 0x09: // Ping
                    SendPong(payload);
                    return null;

                case 0x0A: // Pong (ignore)
                    return null;

                default:
                    return null;
            }
        }

        private byte[] BuildTextFrame(byte[] payload)
        {
            int headerSize;
            byte[] frame;

            if (payload.Length < 126)
            {
                headerSize = 2 + 4; // 2 header + 4 mask
                frame = new byte[headerSize + payload.Length];
                frame[0] = 0x81; // FIN | TEXT
                frame[1] = (byte)(0x80 | payload.Length);
            }
            else if (payload.Length < 65536)
            {
                headerSize = 4 + 4;
                frame = new byte[headerSize + payload.Length];
                frame[0] = 0x81;
                frame[1] = 0xFE; // MASK | 126
                frame[2] = (byte)((payload.Length >> 8) & 0xFF);
                frame[3] = (byte)(payload.Length & 0xFF);
            }
            else
            {
                headerSize = 10 + 4;
                frame = new byte[headerSize + payload.Length];
                frame[0] = 0x81;
                frame[1] = 0xFF; // MASK | 127
                for (int i = 0; i < 8; i++)
                    frame[2 + i] = (byte)((payload.Length >> (56 - i * 8)) & 0xFF);
            }

            // Write mask key
            byte[] mask = new byte[4];
            _random.NextBytes(mask);
            Array.Copy(mask, 0, frame, headerSize - 4, 4);

            // Mask and write payload
            for (int i = 0; i < payload.Length; i++)
                frame[headerSize + i] = (byte)(payload[i] ^ mask[i % 4]);

            return frame;
        }

        private void SendPong(byte[] payload)
        {
            int len = Math.Min(payload.Length, 125);
            byte[] frame = new byte[6 + len];
            frame[0] = 0x8A; // FIN | PONG
            frame[1] = (byte)(0x80 | len);

            byte[] mask = new byte[4];
            _random.NextBytes(mask);
            Array.Copy(mask, 0, frame, 2, 4);

            for (int i = 0; i < len; i++)
                frame[6 + i] = (byte)(payload[i] ^ mask[i % 4]);

            lock (_sendLock)
            {
                try { _stream.Write(frame, 0, frame.Length); }
                catch { }
            }
        }

        private byte[] ReadExact(int count)
        {
            if (count == 0) return new byte[0];

            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = _stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                {
                    _connected = false;
                    return null;
                }
                offset += read;
            }
            return buffer;
        }

        private void EnqueueError(string error)
        {
            lock (_errorQueue)
            {
                _errorQueue.Enqueue(error);
            }
        }
    }
}
