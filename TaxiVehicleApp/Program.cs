using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using SharedClasses.Models;
using SharedClasses.Enums;
using SharedClasses;
using System.Net;

namespace TaxiVehicleApp
{
    class Program
    {
        private const int ServerPort = 50000;

        static void Main(string[] args)
        {
            Console.Title = "TaxiVehicleApp";
            Console.Write("Enter Vehicle ID: ");
            int id;
            while (true)
            {
                var input = Console.ReadLine();
                if (int.TryParse(input, out id) && id >= 0) break;
                Console.WriteLine("ERROR: Vehicle ID must be a non-negative integer. Please try again.");
            }

            // random start within 20×20 (0..19)
            var rnd = new Random();
            var start = new Coordinate(
                rnd.Next(0, SimulationConfig.GridSize),
                rnd.Next(0, SimulationConfig.GridSize));

            var vehicle = new TaxiVehicle
            {
                Id = id,
                Position = start,
                Status = RideStatus.Available
            };

            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var serverEP = new IPEndPoint(IPAddress.Loopback, ServerPort);

            try
            {
                clientSocket.Connect(serverEP);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: cannot connect to server: {ex.Message}");
                Console.WriteLine("Press ENTER to exit...");
                Console.ReadLine();
                return;
            }

            // send initial snapshot (BinaryFormatter)
            try
            {
                byte[] buffer;
                using (var ms = new MemoryStream())
                {
                    var bf = new BinaryFormatter();
                    bf.Serialize(ms, vehicle);
                    buffer = ms.ToArray();
                }
                clientSocket.Send(buffer);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: sending vehicle snapshot failed: {ex.Message}");
                Console.WriteLine("Press ENTER to exit...");
                Console.ReadLine();
                return;
            }

            Console.Clear();
            Console.WriteLine($"VEHICLE {id} connected. Position: ({start.X},{start.Y})");
            Console.WriteLine("Waiting for assignments... (Ctrl+C to exit)");

            // === BLOKIRAJUĆA PETLJA: čeka TaskAssignment preko TCP 
            var recvBuf = new byte[1024];
            while (true)
            {
                int read;
                try
                {
                    read = clientSocket.Receive(recvBuf); // blocking
                    if (read <= 0)
                    {
                        Console.WriteLine("[Vehicle] Server closed connection.");
                        break;
                    }
                }
                catch (SocketException ex)
                {
                    Console.WriteLine($"[Vehicle] Receive error: {ex.Message}");
                    break;
                }

                try
                {
                    using (var ms = new MemoryStream(recvBuf, 0, read))
                    {
                        var bf = new BinaryFormatter();
                        var obj = bf.Deserialize(ms);

                        if (obj is TaskAssignment ta)
                        {
                            Console.WriteLine($"[Vehicle {id}] Assignment -> Client {ta.Request.ClientId} " +
                                              $"from ({ta.Request.From.X},{ta.Request.From.Y}) " +
                                              $"to ({ta.Request.To.X},{ta.Request.To.Y})");
                            Console.WriteLine("[Vehicle] (Next step) simulate movement + send status...");
                            // (sledeći korak: NaPutu -> UVoznji -> završetak)
                        }
                        else
                        {
                            Console.WriteLine("[Vehicle] Unknown object received.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Vehicle] Deserialize error: " + ex.Message);
                }
            }

            try { clientSocket.Close(); } catch { }
            Console.WriteLine("Vehicle shutting down. Press ENTER to exit...");
            Console.ReadLine();
        }

    }
}
