using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Formatters.Binary;
using SharedClasses;           
using SharedClasses.Models;

namespace ClientApp
{
    class Program
    {
        private const string ServerHost = "127.0.0.1";
        private const int ServerUdpPort = 50001;
        static void Main(string[] args)
        {
            Console.Title = "ClientApp";
            Console.Write("Enter Client ID: ");
            int clientId;
            while(true)
            {
                var input = Console.ReadLine();
                if (int.TryParse(input, out clientId) && clientId >= 0) break;
                Console.WriteLine("ERROR: Client ID must be a non-negative integer. Please try again.");
            }
            int max = SimulationConfig.GridSize - 1;
            Console.Write($"Enter start X (0..{max}): "); int sx = ReadIntInRange(0, max);
            Console.Write($"Enter start Y (0..{max}): "); int sy = ReadIntInRange(0, max);
            Console.Write($"Enter dest  X (0..{max}): "); int dx = ReadIntInRange(0, max);
            Console.Write($"Enter dest  Y (0..{max}): "); int dy = ReadIntInRange(0, max);

            var req = new ClientRequest
            {
                ClientId = clientId,
                From = new Coordinate(sx, sy),
                To = new Coordinate(dx, dy)
            };

            try
            {
                using (var udp = new UdpClient())
                {
                    var serverEP = new IPEndPoint(IPAddress.Parse(ServerHost), ServerUdpPort);

                    // binarna serijalizacija zahteva (isti princip kao u njihovom projektu)
                    byte[] payload;
                    using (var ms = new MemoryStream())
                    {
                        var bf = new BinaryFormatter();
                        bf.Serialize(ms, req);
                        payload = ms.ToArray();
                    }

                    udp.Send(payload, payload.Length, serverEP);
                    udp.Client.ReceiveTimeout = 3000; // 3s

                    IPEndPoint remote = null;
                    var resp = udp.Receive(ref remote);
                    Console.WriteLine("[Client] Server replied: " + Encoding.UTF8.GetString(resp));
                }
            }
            catch (SocketException ex)
            {
                Console.WriteLine("ERROR: UDP send/receive failed: " + ex.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
            }

            Console.WriteLine("Press ENTER to exit...");
            Console.ReadLine();

        }
        private static int ReadIntInRange(int min, int max)
        {
            while (true)
            {
                var s = Console.ReadLine();
                int v;
                if (int.TryParse(s, out v) && v >= min && v <= max) return v;
                Console.WriteLine($"ERROR: Enter an integer in range [{min}..{max}]. Try again:");
            }
        }
    }
}
