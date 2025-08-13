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

            // random start 0..19 (20x20)
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
                Console.ReadLine();
                return;
            }

            // 1) Pošalji početni snapshot 
            SendVehicle(clientSocket, vehicle);

            Console.Clear();
            Console.WriteLine($"VEHICLE {id} connected. Position: ({start.X},{start.Y})");
            Console.WriteLine("Waiting for assignments... (Ctrl+C to exit)");

            var recvBuf = new byte[1024];

            while (true)
            {
                int read;
                try
                {
                    read = clientSocket.Receive(recvBuf); // blocking čekanje zadatka
                    if (read <= 0) 
                    { 
                        Console.WriteLine("[Vehicle] Server closed."); 
                        break; 
                    }
                }
                catch (SocketException ex)
                {
                    Console.WriteLine($"[Vehicle] Receive error: {ex.Message}");
                    break;
                }

                object obj = null;
                try
                {
                    using (var ms = new MemoryStream(recvBuf, 0, read))
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
                                      $"from ({ta.Request.From.X},{ta.Request.From.Y}) " +
                                      $"to ({ta.Request.To.X},{ta.Request.To.Y})");

                    // 2) Do klijenta
                    vehicle.Status = RideStatus.GoingToPickup; 
                    SimulateMove(clientSocket, vehicle, ta.Request.From);

                    // 3) Do odredišta
                    vehicle.Status = RideStatus.InRide; 
                    SendVehicle(clientSocket, vehicle);
                    SimulateMove(clientSocket, vehicle, ta.Request.To);

                    // 4) Završetak – ponovo slobodan i pošalji status vožnje
                    vehicle.Status = RideStatus.Available;
                    SendVehicle(clientSocket, vehicle);

                    var status = new RideStatusUpdate
                    {
                        ClientId = ta.Request.ClientId,
                        VehicleId = vehicle.Id,
                        Km = ta.EstimatedSteps,       // koristimo procenu sa servera)
                        Fare = ta.EstimatedSteps * 80m // 80 RSD po "km"  80m = decimal literal
                    };
                    SendRideStatus(clientSocket, status);

                    Console.WriteLine($"[Vehicle {id}] Ride finished. Fare: {status.Fare:0} RSD");
                }
                else
                {
                    Console.WriteLine("[Vehicle] Unknown object received.");
                }
            }

            try { clientSocket.Close(); } catch { }
            Console.WriteLine("Vehicle shutting down. Press ENTER to exit...");
            Console.ReadLine();
        }

        // === helpers ===

        // pomeri po 1 korak po osi X i/ili Y; posle svakog koraka šalje snapshot serveru
        private static void SimulateMove(Socket sock, TaxiVehicle v, Coordinate target)
        {
            while (v.Position.X != target.X || v.Position.Y != target.Y)
            {
                if (v.Position.X < target.X) v.Position.X++;
                else if (v.Position.X > target.X) v.Position.X--;
                if (v.Position.Y < target.Y) v.Position.Y++;
                else if (v.Position.Y > target.Y) v.Position.Y--;

                SendVehicle(sock, v);       // update servera (pozicija + status)
                Thread.Sleep(800);          // 0.8 s po koraku
            }
        }

        private static void SendVehicle(Socket sock, TaxiVehicle v)
        {
            try
            {
                byte[] buffer;
                using (var ms = new MemoryStream())
                {
                    var bf = new BinaryFormatter();
                    bf.Serialize(ms, v);
                    buffer = ms.ToArray();
                }
                sock.Send(buffer);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Vehicle] Send vehicle error: " + ex.Message);
            }
        }

        private static void SendRideStatus(Socket sock, RideStatusUpdate s)
        {
            try
            {
                byte[] buffer;
                using (var ms = new MemoryStream())
                {
                    var bf = new BinaryFormatter();
                    bf.Serialize(ms, s);
                    buffer = ms.ToArray();
                }
                sock.Send(buffer);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Vehicle] Send status error: " + ex.Message);
            }
        }

    }
}
