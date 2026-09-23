using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace MirBot.Tests
{
    public sealed class NotifierTests
    {
        [Fact]
        public async Task RepeatsInsideTheCooldownRideAlongOnTheNextMessage()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            List<JsonElement> received = new List<JsonElement>();
            Task server = Task.Run(async () =>
            {
                for (int i = 0; i < 2; i++)
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync();
                    using NetworkStream stream = client.GetStream();
                    received.Add(await ReadJsonBody(stream));
                    byte[] ok = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(ok);
                }
            });

            using (Notifier notify = new Notifier($"http://127.0.0.1:{port}/api/webhook/test", null))
            {
                TimeSpan cooldown = TimeSpan.FromMilliseconds(300);
                notify.Send("Mirbot3", "Jill", "death", "Jill died", "first", cooldown);
                notify.Send("Mirbot3", "Jill", "death", "Jill died", "second", cooldown);
                notify.Send("Mirbot3", "Jill", "death", "Jill died", "third", cooldown);
                await Task.Delay(400);
                notify.Send("Mirbot3", "Jill", "death", "Jill died", "fourth", cooldown);

                await Task.WhenAny(server, Task.Delay(5000));
            }

            listener.Stop();

            Assert.Equal(2, received.Count);
            Assert.Equal("first", received[0].GetProperty("message").GetString());
            Assert.StartsWith("fourth (+2 more since", received[1].GetProperty("message").GetString());
            Assert.Equal("Jill", received[1].GetProperty("character").GetString());
        }

        [Fact]
        public void WithoutAUrlNothingIsSent()
        {
            using Notifier notify = new Notifier("", null);
            Assert.False(notify.Enabled);
            notify.Send("b", "c", "k", "t", "m", TimeSpan.Zero);
        }

        private static async Task<JsonElement> ReadJsonBody(NetworkStream stream)
        {
            StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            int length = 0;
            string line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line.Substring(15).Trim());

            char[] body = new char[length];
            int read = 0;
            while (read < length)
            {
                int n = await reader.ReadAsync(body, read, length - read);
                if (n == 0) break;
                read += n;
            }

            return JsonDocument.Parse(new string(body, 0, read)).RootElement.Clone();
        }
    }
}
