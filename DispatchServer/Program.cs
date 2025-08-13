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
using SharedClasses;

namespace DispatchServer
{
    internal class Program
    {
        // TCP (vehicles) i UDP (clients) portovi
        private const int VehicleTcpPort = 50000;
        private const int ClientUdpPort = 50001;

        // Sockets
        private static Socket _serverTcp; // vozila (TCP)
        private static Socket _serverUdp; // klijenti (UDP)

        // Buffers
        private static readonly byte[] _bufVehicle = new byte[1024];
        private static readonly byte[] _bufClient = new byte[1024];

        // Evidencije
        private static readonly List<Socket> _vehicleSockets = new List<Socket>();         // aktivni TCP sockets vozila
        private static readonly Dictionary<int, TaxiVehicle> _activeVehicles = new Dictionary<int, TaxiVehicle>(); // ID -> stanje vozila
        private static readonly Dictionary<int, Socket> _socketByVehicleId = new Dictionary<int, Socket>();        // ID -> socket

        static void Main(string[] args)
        {
            Console.Title = "DispatchServer";

            // TCP za vozila
            _serverTcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _serverTcp.Bind(new IPEndPoint(IPAddress.Any, VehicleTcpPort));
            _serverTcp.Blocking = false;
            _serverTcp.Listen(10);

            // UDP za klijente
            _serverUdp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _serverUdp.Bind(new IPEndPoint(IPAddress.Any, ClientUdpPort));
            _serverUdp.Blocking = false;

            Console.WriteLine($"[Server] TCP listening on {VehicleTcpPort} (vehicles)");
            Console.WriteLine($"[Server] UDP bound on {ClientUdpPort} (clients)");

            PrintStatus();

            while (true)
            {
                try
                {
                    var checkRead = new List<Socket>();
                    checkRead.Add(_serverTcp);
                    checkRead.Add(_serverUdp);
                    checkRead.AddRange(_vehicleSockets);

                    // timeout 1 ms (1000 microseconds)
                    Socket.Select(checkRead, null, null, 1000);

                    foreach (var sock in checkRead)
                    {
                        if (sock == _serverTcp)
                        {
                            // Nova TCP konekcija vozila
                            try
                            {
                                var vehicleSock = _serverTcp.Accept();
                                vehicleSock.Blocking = false;
                                _vehicleSockets.Add(vehicleSock);
                                Console.WriteLine($"[Server] Vehicle connected: {vehicleSock.RemoteEndPoint}");
                                PrintStatus();
                            }
                            catch (SocketException ex)
                            {
                                Console.WriteLine($"[Server] Accept error: {ex.Message}");
                            }
                        }
                        else if (sock == _serverUdp)
                        {
                            // Klijent šalje ClientRequest (UDP)
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
                                        // 2) pripremi TaskAssignment (uključuj i procenjene korake do klijenta)
                                        int stepsToClient = Chebyshev(nearest.Position, cr.From);
                                        var ta = new TaskAssignment
                                        {
                                            VehicleId = nearest.Id,
                                            Request = cr,
                                            EstimatedSteps = stepsToClient
                                        };

                                        // 3) pošalji TaskAssignment vozilu (TCP)
                                        Socket vehSock;
                                        if (_socketByVehicleId.TryGetValue(nearest.Id, out vehSock))
                                        {
                                            try
                                            {
                                                byte[] payload;
                                                using (var ms = new MemoryStream())
                                                {
                                                    var bf = new BinaryFormatter();
                                                    bf.Serialize(ms, ta);
                                                    payload = ms.ToArray();
                                                }

                                                int sent = vehSock.Send(payload);
                                                Console.WriteLine($"[Server] TaskAssignment sent to vehicle {nearest.Id} ({sent} bytes) for client {cr.ClientId}");

                                                // 4) pošalji ETA klijentu (UDP) — brzina 0.8 koraka/s (kao u referentnom projektu)
                                                double etaSec = stepsToClient / 0.8;
                                                replyText = $"Vehicle {nearest.Id} assigned. ETA ≈ {etaSec:0}s";
                                            }
                                            catch (SocketException se)
                                            {
                                                Console.WriteLine("[Server] Error sending TaskAssignment: " + se.SocketErrorCode);
                                                replyText = "Error assigning vehicle";
                                            }
                                            catch (Exception ex)
                                            {
                                                Console.WriteLine("[Server] Error sending TaskAssignment: " + ex.Message);
                                                replyText = "Error assigning vehicle";
                                            }
                                        }
                                        else
                                        {
                                            Console.WriteLine($"[Server] Vehicle socket not found for ID {nearest.Id}");
                                            replyText = "Vehicle socket not found";
                                        }
                                    }
                                }
                                else
                                {
                                    // fallback: plain text
                                    var txt = Encoding.UTF8.GetString(_bufClient, 0, read);
                                    Console.WriteLine($"[Server] UDP (text) from {clientEp}: {txt}");
                                }

                                // UDP odgovor klijentu (ACK/ETA/poruka)
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
                            // Poruka od povezanog vozila (TCP)
                            try
                            {
                                int read = sock.Receive(_bufVehicle);
                                if (read <= 0)
                                {
                                    // vozilo se diskonektovalo
                                    Console.WriteLine($"[Server] Vehicle disconnected: {sock.RemoteEndPoint}");

                                    // ukloni iz mapa po ID-u
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

                                // deserijalizuj objekt sa vozila
                                object obj;
                                using (var ms = new MemoryStream(_bufVehicle, 0, read))
                                {
                                    var bf = new BinaryFormatter();
                                    obj = bf.Deserialize(ms);
                                }

                                if (obj is TaxiVehicle tv)
                                {
                                    // ažuriraj stanje vozila
                                    _activeVehicles[tv.Id] = tv;
                                    _socketByVehicleId[tv.Id] = sock;
                                    Console.WriteLine($"[Server] Vehicle {tv.Id} @ ({tv.Position.X},{tv.Position.Y}) {tv.Status}");
                                    PrintStatus();
                                }
                                else if (obj is RideStatusUpdate rs)
                                {
                                    // vozilo javlja završetak vožnje (km/cena)
                                    TaxiVehicle v;
                                    if (_activeVehicles.TryGetValue(rs.VehicleId, out v))
                                    {
                                        v.Kilometers += rs.Km;
                                        v.Earnings += rs.Fare;
                                        v.PassengersServed += 1;
                                    }

                                    Console.WriteLine($"[Server] Ride finished: vehicle {rs.VehicleId}, client {rs.ClientId}, km={rs.Km:0}, fare={rs.Fare:0} RSD");
                                    PrintStatus();
                                }
                                else
                                {
                                    Console.WriteLine("[Server] Unknown object received from vehicle.");
                                }
                            }
                            catch (SocketException)
                            {
                                // prisilno zatvaranje / greška
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
                            catch (Exception ex)
                            {
                                Console.WriteLine("[Server] Vehicle message error: " + ex.Message);
                            }
                        }
                    }
                }
                catch (Exception ex)
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
                Console.WriteLine($" - ID {v.Id}: {v.Status} @ ({v.Position.X},{v.Position.Y})  Km={v.Kilometers:0}  Earn={v.Earnings:0}  Psg={v.PassengersServed}");
            Console.WriteLine();
        }

        // ===== Helpers =====

        // Euklidsko rastojanje (za izbor najbližeg)
        private static double Euclidean(Coordinate a, Coordinate b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // Chebyshev rastojanje (broj “koraka” po mreži) – koristi se i za ETA
        private static int Chebyshev(Coordinate a, Coordinate b)
        {
            int dx = Math.Abs(a.X - b.X);
            int dy = Math.Abs(a.Y - b.Y);
            return (dx > dy) ? dx : dy;
        }

        // Najbliže vozilo koje je Available
        private static TaxiVehicle FindNearestAvailable(Coordinate from)
        {
            TaxiVehicle best = null;
            double bestDist = double.MaxValue;

            foreach (var v in _activeVehicles.Values)
            {
                if (v.Status == RideStatus.Available)
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
