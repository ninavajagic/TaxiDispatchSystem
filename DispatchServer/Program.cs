using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using SharedClasses.Models;
using SharedClasses.Enums;

namespace DispatchServer
{
    internal class Program
    {
        //TCP (vozila) i UDP (klijenti) portovi
        private const int VehicleTcpPort = 50000;
        private const int ClientUdpPort = 50001;

        // Sockets
        private static Socket _serverTcp; //vozila
        private static Socket _serverUdp; //klijenti

        //Buffers
        private static readonly byte[] _bufVehicle = new byte[1024];
        private static readonly byte[] _bufClient = new byte[1024];

        //Evidencije
        private static readonly List<Socket> _vehicleSockets = new List<Socket>();
        private static readonly Dictionary<int, TaxiVehicle> _activeVehicles = new Dictionary<int, TaxiVehicle>();
        private static readonly Dictionary<int, Socket> _socketByVehicleId = new Dictionary<int, Socket>();

        static void Main(string[] args)
        {
            Console.Title = "DispatchServer";

            //TCP za vozila
            _serverTcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _serverTcp.Bind(new IPEndPoint(IPAddress.Any, VehicleTcpPort));
            _serverTcp.Blocking = false;
            _serverTcp.Listen(10);

            //UDP za klijente
            _serverUdp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _serverUdp.Bind(new IPEndPoint(IPAddress.Any, ClientUdpPort));
            _serverUdp.Blocking = false;

            Console.WriteLine($"[Server] TCP listening on {VehicleTcpPort} (vehicles)");
            Console.WriteLine($"[Server] UDP bound on {ClientUdpPort} (clients)");

            PrintStatus();

            while(true)
            {
                try
                {
                    var checkRead = new List<Socket>();
                    //slusamo na glavnim socketima
                    checkRead.Add(_serverTcp);
                    checkRead.Add(_serverUdp);
                    //...i na svim vozilima koja su vec povezana
                    checkRead.AddRange(_vehicleSockets);

                    //timeout 1ms
                    Socket.Select(checkRead, null, null, 1000);

                    foreach(var sock in checkRead)
                    {
                        if(sock == _serverTcp)
                        {
                            //Nova TCP konekcija vozila
                            try
                            {
                                var vehicle = _serverTcp.Accept();
                                vehicle.Blocking = false;
                                _vehicleSockets.Add(vehicle);
                                Console.WriteLine($"[Server] Vehicle connected: {vehicle.RemoteEndPoint}");
                                PrintStatus();
                            }
                            catch (SocketException ex)
                            {
                                Console.WriteLine($"[Server] Accept error: " + ex.Message);
                            }
                        }
                        else if (sock == _serverUdp)
                        {
                            try
                            {
                                EndPoint clientEp = new IPEndPoint(IPAddress.Any, 0);
                                int read = _serverUdp.ReceiveFrom(_bufClient, ref clientEp);

                                object obj;
                                using (var ms = new MemoryStream(_bufClient, 0, read))
                                {
                                    var bf = new BinaryFormatter();
                                    obj = bf.Deserialize(ms);
                                }

                                string replyText = "REQUEST RECEIVED";
                                if (obj is ClientRequest cr)
                                {
                                    Console.WriteLine($"[Server] UDP ClientRequest #{cr.ClientId}: from ({cr.From.X},{cr.From.Y}) to ({cr.To.X},{cr.To.Y})");

                                    // 1) nađi najbliže Available vozilo
                                    var nearest = FindNearestAvailable(cr.From);
                                    if (nearest == null)
                                    {
                                        replyText = "No available vehicles at the moment";
                                    }
                                    else
                                    {
                                        // 2) pošalji TaskAssignment tom vozilu preko njegovog TCP socketa
                                        var ta = new TaskAssignment { VehicleId = nearest.Id, Request = cr };

                                        byte[] payload;
                                        using (var ms = new MemoryStream())
                                        {
                                            var bf = new BinaryFormatter();
                                            bf.Serialize(ms, ta);
                                            payload = ms.ToArray();
                                        }

                                        Socket vehSock;
                                        if (_socketByVehicleId.TryGetValue(nearest.Id, out vehSock))
                                        {
                                            try
                                            {
                                                vehSock.Send(payload);
                                                // 3) izračunaj ETA (broj Chebyshev koraka / brzina 0.8 koraka u sekundi)
                                                int steps = Chebyshev(nearest.Position, cr.From);
                                                double etaSec = steps / 0.8;
                                                replyText = $"Vehicle {nearest.Id} assigned. ETA ≈ {etaSec:0}s";
                                            }
                                            catch (Exception ex)
                                            {
                                                Console.WriteLine("[Server] Error sending TaskAssignment: " + ex.Message);
                                                replyText = "Error assigning vehicle";
                                            }
                                        }
                                        else
                                        {
                                            replyText = "Vehicle socket not found";
                                        }
                                    }
                                }
                                else
                                {
                                    // fallback kada stigne čist tekst
                                    var txt = Encoding.UTF8.GetString(_bufClient, 0, read);
                                    Console.WriteLine($"[Server] UDP (text) from {clientEp}: {txt}");
                                }

                                var reply = Encoding.UTF8.GetBytes(replyText);
                                _serverUdp.SendTo(reply, clientEp);
                            }
                            catch (SocketException ex)
                            {
                                Console.WriteLine("[Server] UDP receive error: " + ex.Message);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("[Server] UDP deserialize/handle error: " + ex.Message);
                            }
                        }

                        else
                        {
                            //Poruka od povezanog vozila (TCP)
                            try
                            {
                                int read = sock.Receive(_bufVehicle);
                                if(read <= 0)
                                {
                                    Console.WriteLine($"[Server] Vehicle disconnected: {sock.RemoteEndPoint}");
                                    // uklanjanje iz evidencija 
                                    int removeId = -1;
                                    foreach (var kv in _socketByVehicleId)
                                    {
                                        if (kv.Value == sock) { removeId = kv.Key; break; }
                                    }
                                    if (removeId != -1)
                                    {
                                        _socketByVehicleId.Remove(removeId);
                                        _activeVehicles.Remove(removeId);
                                    }

                                    _vehicleSockets.Remove(sock);
                                    try { sock.Close(); } catch { }

                                    PrintStatus();
                                    continue;
                                }
                                try
                                {
                                    object obj;
                                    using (var ms = new MemoryStream(_bufVehicle, 0, read)) 
                                    {
                                        var bf = new BinaryFormatter();
                                        obj = bf.Deserialize(ms);
                                    }

                                    if (obj is TaxiVehicle tv)
                                    {
                                        _activeVehicles[tv.Id] = tv;    // čuvamo najnovije stanje vozila
                                        _socketByVehicleId[tv.Id] = sock;  // map: ID → socket

                                        Console.WriteLine($"[Server] Vehicle {tv.Id} @ ({tv.Position.X},{tv.Position.Y}) {tv.Status}");
                                        PrintStatus();
                                    }
                                    else
                                    {
                                        Console.WriteLine("[Server] Unknown object received from vehicle.");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine("[Server] Deserialize error: " + ex.Message);
                                }
                            }
                            catch (SocketException)
                            {
                                // uklanjanje iz evidencija 
                                int removeId = -1;
                                foreach (var kv in _socketByVehicleId)
                                {
                                    if (kv.Value == sock) { removeId = kv.Key; break; }
                                }
                                if (removeId != -1)
                                {
                                    _socketByVehicleId.Remove(removeId);
                                    _activeVehicles.Remove(removeId);
                                }

                                _vehicleSockets.Remove(sock);
                                try { sock.Close(); } catch { }

                                Console.WriteLine("[Server] Vehicle closed (exception).");
                                PrintStatus();
                            }
                        }
                    }
                }
                catch(Exception ex)
                {
                    Console.WriteLine("[Server] Loop error: " + ex.Message);
                }
            }

        }

        private static void PrintStatus()
        {
            Console.WriteLine();
            Console.WriteLine("=== TAXI DISPATCH ===");
            Console.WriteLine($"Vehicles connected (sockets): {_vehicleSockets.Count}");
            foreach (var v in _activeVehicles.Values.OrderBy(v => v.Id))
                Console.WriteLine($" - ID {v.Id}: {v.Status} @ ({v.Position.X},{v.Position.Y})");
            Console.WriteLine();
        }

        // Euklidsko rastojanje za izbor najbližeg 
        private static double Euclidean(Coordinate a, Coordinate b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // Chebyshev rastojanje (koliko "koraka" do tačke) – za ETA
        private static int Chebyshev(Coordinate a, Coordinate b)
        {
            return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        // Pronađi najbliže vozilo koje je Available
        private static TaxiVehicle FindNearestAvailable(Coordinate from)
        {
            TaxiVehicle best = null;
            double bestDist = double.MaxValue;
            foreach (var v in _activeVehicles.Values)
            {
                if (v.Status == SharedClasses.Enums.RideStatus.Available)
                {
                    double d = Euclidean(v.Position, from);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = v;
                    }
                }
            }
            return best;
        }



    }
}
