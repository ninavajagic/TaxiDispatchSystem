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
            while (!int.TryParse(Console.ReadLine(), out clientId) || clientId < 0)
                Console.Write("ERROR: enter non-negative integer: ");

            Console.Write("From X (0..19): ");
            int fx = ReadCoord();
            Console.Write("From Y (0..19): ");
            int fy = ReadCoord();

            Console.Write("To   X (0..19): ");
            int tx = ReadCoord();
            Console.Write("To   Y (0..19): ");
            int ty = ReadCoord();

            var req = new ClientRequest
            {
                ClientId = clientId,
                From = new Coordinate(fx, fy),
                To = new Coordinate(tx, ty)
            };

            try
            {
                using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    var serverEp = new IPEndPoint(IPAddress.Parse(ServerHost), ServerUdpPort);

                    // Pošalji binarno
                    byte[] payload;
                    using (var ms = new MemoryStream())
                    {
                        var bf = new BinaryFormatter();
                        bf.Serialize(ms, req);
                        payload = ms.ToArray();
                    }
                    udp.SendTo(payload, serverEp);
                    Console.WriteLine("[Client] Request sent.");

                    // Slušaj poruke servera
                    EndPoint any = new IPEndPoint(IPAddress.Any, 0);
                    var buf = new byte[1024];

                    Console.WriteLine("[Client] Waiting for server updates (ETA/approaching/arrival)...");
                    udp.ReceiveTimeout = 30000; // 30s po poruci

                    while (true)
                    {
                        try
                        {
                            int read = udp.ReceiveFrom(buf, ref any);
                            var msg = Encoding.UTF8.GetString(buf, 0, read);
                            Console.WriteLine("[Server → Client] " + msg);

                            if (msg.StartsWith("Arrived at destination!", StringComparison.OrdinalIgnoreCase))
                            {
                                break; // vožnja gotova
                            }
                        }
                        catch (SocketException)
                        {
                            // timeout – izađi ili nastavi; ovde izlazimo
                            Console.WriteLine("[Client] No more updates (timeout).");
                            break;
                        }
                    }

                    Console.WriteLine("Press ENTER to exit...");
                    Console.ReadLine();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                Console.ReadLine();
            }
        }

        private static int ReadCoord()
        {
            int c;
            while (!int.TryParse(Console.ReadLine(), out c) || c < 0 || c >= 20)
                Console.Write("ERROR: enter 0..19: ");
            return c;
        }
    }
}
