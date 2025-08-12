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

namespace TaxiVehicleApp
{
    class Program
    {
        private const string ServerHost = "127.0.0.1";
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
            // nasumična početna lokacija u opsegu 0..GridSize-1 (za 30×30 → 0..29)
            var rnd = new Random();
            var start = new Coordinate(
                rnd.Next(0, SimulationConfig.GridSize),
                rnd.Next(0, SimulationConfig.GridSize));

            var vehicle = new TaxiVehicle
            {
                Id = id,                      // ID vozila
                Position = start,             // početna pozicija
                Status = RideStatus.Available // vozilo je slobodno
            };

            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect(ServerHost, ServerPort);
                    Console.WriteLine($"[Vehicle {id}] Connected to server {ServerHost}:{ServerPort}");

                    var stream = client.GetStream();
                    // Pošalji binarni snapshot vozila (BinaryFormatter, kao u originalnom projektu)
                    using (var ms = new MemoryStream())
                    {
                        var bf = new BinaryFormatter();
                        bf.Serialize(ms, vehicle);
                        var bytes = ms.ToArray();
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    }

                    Console.Clear();
                    Console.WriteLine($"VEHICLE {id} connected. Position: ({start.X},{start.Y})");
                    Console.WriteLine("For now, tasks are not processed (next step). Press ENTER to exit...");
                    Console.ReadLine();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: Unable to connect/send to server. Details: " + ex.Message);
                Console.WriteLine("Press ENTER to exit...");
                Console.ReadLine();
            }
        }
    }
}
