﻿using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Timers;
using System.Web.Script.Serialization;
using WebSocketSharp;
using XY = System.ValueTuple<byte, byte>;

namespace PixelPlanetBot
{
    class InteractionWrapper : IDisposable
    {
        private const byte subscribeOpcode = 161;
        private const byte unsubscribeOpcode = 162;
        private const byte pixelUpdatedOpcode = 193;

        private const string baseHttpAdress = "https://pixelplanet.fun";
        private const string webSocketUrlTemplate = "wss://pixelplanet.fun/ws?fingerprint={0}";

        private readonly string fingerprint;

        private WebSocket webSocket;
        private readonly string wsUrl;
        private readonly Timer wsConnectionDelayTimer = new Timer(5000D);
        private readonly Timer wsPingTimer = new Timer(10000D);
        private HashSet<XY> TrackedChunks = new HashSet<XY>();

        
        private bool disposed = false;
        public event EventHandler<PixelChangedEventArgs> OnPixelChanged;

        public InteractionWrapper(string fingerprint, string proxyFingerprint = null, WebProxy proxy = null)
        {
            this.fingerprint = fingerprint;
            wsUrl = string.Format(webSocketUrlTemplate, fingerprint);
            wsPingTimer.Elapsed += PingTimer_Elapsed;
            wsConnectionDelayTimer.Elapsed += ConnectionDelayTimer_Elapsed;
            using (HttpWebResponse response = SendJsonRequest("api/me", new { fingerprint }))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception("Cannot connect to API");
                }
            }
            webSocket = new WebSocket(wsUrl);
            webSocket.Log.Output = (d, s) => { };
            webSocket.OnOpen += WebSocket_OnOpen;
            webSocket.OnMessage += WebSocket_OnMessage;
            webSocket.OnError += WebSocket_OnError;
            webSocket.OnClose += WebSocket_OnClose;
            Connect();
        }



        public void UnsubscribeFromUpdates(XY chunk)
        {
            if (TrackedChunks.Contains(chunk))
            {
                byte[] data = new byte[3]
                {
                    unsubscribeOpcode,
                    chunk.Item1,
                    chunk.Item2
                };
                webSocket.Send(data);
            }
        }

        public void SubscribeToUpdates(XY chunk)
        {
            TrackedChunks.Add(chunk);
            byte[] data = new byte[3]
            {
                    subscribeOpcode,
                    chunk.Item1,
                    chunk.Item2
            };
            webSocket.Send(data);
        }

        public bool PlacePixel(int x, int y, PixelColor color, out double coolDown)
        {
            var data = new
            {
                x,
                y,
                a = x + y - 8,
                color = (byte)color,
                fingerprint,
                token = "null"
            };
            using (HttpWebResponse response = SendJsonRequest("api/pixel", data))
            {
                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        {
                            using (StreamReader sr = new StreamReader(response.GetResponseStream()))
                            {
                                string responseString = sr.ReadToEnd();
                                JObject json = JObject.Parse(responseString);
                                if (bool.TryParse(json["success"].ToString(), out bool success) && success)
                                {
                                    coolDown = double.Parse(json["coolDownSeconds"].ToString());
                                    return true;
                                }
                                else
                                {
                                    if (json["errors"].Count() > 0)
                                    {
                                        string errors = string.Concat(json["errors"].Select(e => $"{Environment.NewLine}\"{e}\""));
                                        throw new Exception($"Server responded with errors:{errors}");
                                    }
                                    else
                                    {
                                        coolDown = double.Parse(json["waitSeconds"].ToString());
                                        return false;
                                    }
                                }
                            }
                        }
                    case HttpStatusCode.Forbidden:
                        throw new Exception("Action was forbidden by pixelworld itself");
                    default:
                        coolDown = 30D;
                        return false;
                }
            }
        }

        public PixelColor[,] GetChunk(XY chunk)
        {
            try
            {
                string url = $"{baseHttpAdress}/chunks/{chunk.Item1}/{chunk.Item2}.bin";
                using (WebClient wc = new WebClient())
                {
                    byte[] pixelData = wc.DownloadData(url);
                    PixelColor[,] map = new PixelColor[PixelMap.ChunkSize, PixelMap.ChunkSize];
                    if (pixelData.Length == 0)
                    {
                        return map;
                    }
                    int i = 0;
                    for (int y = 0; y < PixelMap.ChunkSize; y++)
                    {
                        for (int x = 0; x < PixelMap.ChunkSize; x++)
                        {
                            map[x, y] = (PixelColor)pixelData[i++];
                        }
                    }
                    return map;
                }
            }
            catch
            {
                throw new Exception("Cannot download chunk data");
            }
        }


        private HttpWebResponse SendJsonRequest(string relativeUrl, object data)
        {
            HttpWebRequest request = WebRequest.CreateHttp($"{baseHttpAdress}/{relativeUrl}");
            request.Method = "POST";
            using (Stream requestStream = request.GetRequestStream())
            {
                using (StreamWriter streamWriter = new StreamWriter(requestStream))
                {
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    string jsonText = serializer.Serialize(data);
                    streamWriter.Write(jsonText);
                }
            }
            request.ContentType = "application/json";
            request.Headers["Origin"] = request.Referer = baseHttpAdress;
            request.UserAgent = "Mozilla / 5.0(X11; Linux x86_64; rv: 57.0) Gecko / 20100101 Firefox / 57.0";
            return request.GetResponse() as HttpWebResponse;
        }

        private void Connect()
        {
            if (webSocket?.ReadyState != WebSocketState.Open)
            {
                Program.LogLineToConsole("Connecting via websocket...", ConsoleColor.Yellow);
                webSocket.Connect();
            }
        }

        private void PingTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (!webSocket.Ping())
            {
                webSocket.Close();
            }
        }

        private void ConnectionDelayTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (webSocket?.ReadyState == WebSocketState.Connecting ||
                webSocket?.ReadyState == WebSocketState.Open)
            {
                 wsConnectionDelayTimer.Stop();
            }
            else
            {
                Connect();
            }
        }

        private void WebSocket_OnClose(object sender, CloseEventArgs e)
        {
            Program.LogLineToConsole("Websocket connection closed, trying to reconnect...", ConsoleColor.Red);
            wsPingTimer.Stop();
            wsConnectionDelayTimer.Start();
        }

        private void WebSocket_OnError(object sender, WebSocketSharp.ErrorEventArgs e)
        {
            webSocket.OnError -= WebSocket_OnError;
            Program.LogLineToConsole("Error on websocket: \n" + e.Message, ConsoleColor.Red);
            webSocket.Close();
            Task.Delay(1000).Wait();
            webSocket.OnError += WebSocket_OnError;
        }

        private void WebSocket_OnMessage(object sender, MessageEventArgs e)
        {
            byte[] buffer = e.RawData;
            if (buffer.Length == 0)
            {
                return;
            }
            if (buffer[0] == pixelUpdatedOpcode)
            {
                byte chunkX = buffer[2];
                byte chunkY = buffer[4];
                byte relativeY = buffer[5];
                byte relativeX = buffer[6];
                byte color = buffer[7];
                PixelChangedEventArgs args = new PixelChangedEventArgs
                {
                    Chunk = (chunkX, chunkY),
                    Pixel = (relativeX, relativeY),
                    Color = (PixelColor)color
                };
                OnPixelChanged?.Invoke(this, args);
            }
        }

        private void WebSocket_OnOpen(object sender, EventArgs e)
        {
            wsPingTimer.Start();
            foreach (XY chunk in TrackedChunks)
            {
                //UnsubscribeFromUpdates(chunk);
                SubscribeToUpdates(chunk);
            }
            Program.LogLineToConsole("Listening for changes via websocket", ConsoleColor.Green);
        }

        public void Dispose()
        {
            if (!disposed)
            {
                webSocket.OnOpen -= WebSocket_OnOpen;
                webSocket.OnMessage -= WebSocket_OnMessage;
                webSocket.OnError -= WebSocket_OnError;
                webSocket.OnClose -= WebSocket_OnClose;
                webSocket.Close();
                (webSocket as IDisposable).Dispose();
                wsConnectionDelayTimer.Dispose();
                wsPingTimer.Dispose();
                OnPixelChanged = null;
            }
            disposed = true;
        }
    }
}
