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
using System.Threading;

namespace TaxiVehicleApp
{
    class Program
    {
        private const string ServerHost = "127.0.0.1";
        private const int ServerPort = 50000;

        // Chebyshev distanca na mreži (max|dx|,|dy|) – ista metrika koju koristi server
        private static int Chebyshev(Coordinate a, Coordinate b)
        {
            return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

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

            // Nasumična početna lokacija u opsegu 0..GridSize-1 (za 20×20 → 0..19)
            var rnd = new Random();
            var start = new Coordinate(
                rnd.Next(0, SimulationConfig.GridSize),
                rnd.Next(0, SimulationConfig.GridSize));

            // Inicijalno stanje vozila – slobodno na start poziciji
            var vehicle = new TaxiVehicle
            {
                Id = id,
                Position = start,
                Status = RideStatus.Available
            };

            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect(ServerHost, ServerPort);
                    var stream = client.GetStream();
                    Console.WriteLine($"[Vehicle {id}] Connected to server {ServerHost}:{ServerPort}");

                    // Pošalji početni snapshot vozila serveru (BinaryFormatter)
                    SendVehicle(stream, vehicle);

                    Console.WriteLine($"[Vehicle {id}] Ready at ({start.X},{start.Y}). Waiting for assignments...");

                    // Petlja prijema zadataka od servera
                    var buf = new byte[1024];
                    while (true)
                    {
                        int read;
                        try
                        {
                            // Blokirajuće čitanje; 
                            read = stream.Read(buf, 0, buf.Length);
                        }
                        catch (IOException)
                        {
                            // timeout ili IO greška – pokušaj nastaviti
                            continue;
                        }

                        if (read <= 0)
                        {
                            Console.WriteLine("[Vehicle] Server closed the connection.");
                            break;
                        }

                        object obj;
                        try
                        {
                            using (var ms = new MemoryStream(buf, 0, read))
                            {
                                var bf = new BinaryFormatter();
                                obj = bf.Deserialize(ms);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[Vehicle] Deserialize error: " + ex.Message);
                            continue;
                        }

                        if (obj is TaskAssignment ta)
                        {
                            Console.WriteLine($"[Vehicle {id}] Assignment -> Client {ta.Request.ClientId} " +
                                              $"from ({ta.Request.From.X},{ta.Request.From.Y}) to ({ta.Request.To.X},{ta.Request.To.Y})");

                            // 1) Kretanje do klijenta
                            vehicle.Status = RideStatus.GoingToPickup; // odlazak na lokaciju klijenta
                            SendVehicle(stream, vehicle);
                            SimulateMovement(vehicle, ta.Request.From, stream);

                            // 2) Prevoz klijenta do odredišta
                            vehicle.Status = RideStatus.InRide; // u vožnji sa klijentom
                            SendVehicle(stream, vehicle);
                            SimulateMovement(vehicle, ta.Request.To, stream);

                            // 3) Povratak u Available i slanje završnog statusa
                            vehicle.Status = RideStatus.Available; // vozilo je opet slobodno
                            SendVehicle(stream, vehicle);

                            // >>> OPCIJA B: km i cena računamo od KLIJENT → ODREDIŠTE (ne vozilo → klijent)
                            int rideKm = Chebyshev(ta.Request.From, ta.Request.To);
                            decimal fare = rideKm * SimulationConfig.PricePerKm;

                            var status = new RideStatusUpdate
                            {
                                VehicleId = id,
                                ClientId = ta.Request.ClientId,
                                Km = rideKm,
                                Fare = fare
                            };

                            using (var ms2 = new MemoryStream())
                            {
                                var bf2 = new BinaryFormatter();
                                bf2.Serialize(ms2, status);
                                var payload2 = ms2.ToArray();
                                stream.Write(payload2, 0, payload2.Length);
                                stream.Flush();
                            }

                            Console.WriteLine($"[Vehicle {id}] Ride finished. Km={rideKm}, Fare={fare} RSD");
                            Console.WriteLine($"[Vehicle {id}] Waiting for next assignment...");
                        }
                        else
                        {
                            Console.WriteLine("[Vehicle] Unknown object received from server.");
                        }
                    }

                    Console.WriteLine("[Vehicle] Press ENTER to exit...");
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

        // Šalje trenutno stanje vozila serveru (binarna serijalizacija)
        private static void SendVehicle(NetworkStream stream, TaxiVehicle vehicle)
        {
            try
            {
                using (var ms = new MemoryStream())
                {
                    var bf = new BinaryFormatter();
                    bf.Serialize(ms, vehicle);
                    var bytes = ms.ToArray();
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Vehicle] Send error: " + ex.Message);
            }
        }

        // Simulira kretanje vozila do cilja; posle svakog koraka šalje stanje serveru
        private static void SimulateMovement(TaxiVehicle vehicle, Coordinate target, NetworkStream stream)
        {
            // Pomera se po 1 u X i/ili Y (kao u referentnom kodu); pauza 0.8s po koraku
            while (vehicle.Position.X != target.X || vehicle.Position.Y != target.Y)
            {
                if (vehicle.Position.X < target.X) vehicle.Position.X++;
                else if (vehicle.Position.X > target.X) vehicle.Position.X--;

                if (vehicle.Position.Y < target.Y) vehicle.Position.Y++;
                else if (vehicle.Position.Y > target.Y) vehicle.Position.Y--;

                SendVehicle(stream, vehicle);
                Thread.Sleep(800); // da se kretanje “vidi”
            }
        }
    }
}
