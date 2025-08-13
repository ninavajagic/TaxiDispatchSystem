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
        private static readonly List<Socket> _vehicleSockets = new List<Socket>();                 // aktivni TCP sockets vozila
        private static readonly Dictionary<int, TaxiVehicle> _activeVehicles = new Dictionary<int, TaxiVehicle>(); // ID -> stanje vozila
        private static readonly Dictionary<int, Socket> _socketByVehicleId = new Dictionary<int, Socket>();        // ID -> socket

        // Klijent ↔ vozilo i aktivni zadatak (za UDP obaveštenja)
        private static readonly Dictionary<int, EndPoint> _clientEpByClientId = new Dictionary<int, EndPoint>();   // ClientId -> UDP EndPoint
        private static readonly Dictionary<int, int> _clientIdByVehicleId = new Dictionary<int, int>();         // VehicleId -> ClientId
        private static readonly Dictionary<int, TaskAssignment> _activeTasksByVehicleId = new Dictionary<int, TaskAssignment>(); // VehicleId -> TA
        private static readonly Dictionary<int, int> _stepCounterByVehicleId = new Dictionary<int, int>();         // VehicleId -> brojač koraka

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

            // >>> dodatak: ignoriši ICMP Port Unreachable (Windows)
            const int SIO_UDP_CONNRESET = -1744830452;
            _serverUdp.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);

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

                    Socket.Select(checkRead, null, null, 1000); // 1ms

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
                                        // 2) pripremi TaskAssignment (+ procenjeni koraci do klijenta)
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

                                                // 4) pošalji ETA klijentu (UDP) — brzina 0.8 koraka/s
                                                double etaSec = stepsToClient / 0.8;
                                                replyText = $"Vehicle {nearest.Id} assigned. ETA ≈ {etaSec:0}s";

                                                // 5) zapamti veze i aktivni zadatak (za dalja UDP obaveštenja)
                                                _clientEpByClientId[cr.ClientId] = clientEp;
                                                _clientIdByVehicleId[nearest.Id] = cr.ClientId;
                                                _activeTasksByVehicleId[nearest.Id] = ta;
                                                _stepCounterByVehicleId[nearest.Id] = 0;
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
                                        _clientIdByVehicleId.Remove(removeId);
                                        _activeTasksByVehicleId.Remove(removeId);
                                        _stepCounterByVehicleId.Remove(removeId);
                                    }

                                    _vehicleSockets.Remove(sock);
                                    try { sock.Close(); } catch { }
                                    PrintStatus();
                                    continue;
                                }

                                // deserijalizuj objekt sa vozila
                                object vobj;
                                using (var ms = new MemoryStream(_bufVehicle, 0, read))
                                {
                                    var bf = new BinaryFormatter();
                                    vobj = bf.Deserialize(ms);
                                }

                                if (vobj is TaxiVehicle tv)
                                {
                                    // ažuriraj stanje vozila
                                    _activeVehicles[tv.Id] = tv;
                                    _socketByVehicleId[tv.Id] = sock;
                                    Console.WriteLine($"[Server] Vehicle {tv.Id} @ ({tv.Position.X},{tv.Position.Y}) {tv.Status}");
                                    PrintStatus();

                                    // --- UDP obaveštenja klijentu tokom dolaska na preuzimanje ---
                                    TaskAssignment currentTa;
                                    if (_activeTasksByVehicleId.TryGetValue(tv.Id, out currentTa))
                                    {
                                        if (tv.Status == RideStatus.GoingToPickup)
                                        {
                                            int cnt = 0;
                                            _stepCounterByVehicleId.TryGetValue(tv.Id, out cnt);
                                            cnt++;
                                            _stepCounterByVehicleId[tv.Id] = cnt;

                                            int distance = Chebyshev(tv.Position, currentTa.Request.From);
                                            if (cnt % 4 == 0 && distance > 2)
                                            {
                                                int clientId;
                                                if (_clientIdByVehicleId.TryGetValue(tv.Id, out clientId))
                                                {
                                                    EndPoint cep;
                                                    if (_clientEpByClientId.TryGetValue(clientId, out cep))
                                                    {
                                                        double eta = distance / 0.8;
                                                        var msg = Encoding.UTF8.GetBytes($"Vehicle is approaching... ETA ≈ {eta:0}s");
                                                        try { _serverUdp.SendTo(msg, cep); } catch { }
                                                    }
                                                }
                                            }

                                            // ako je vozilo stiglo na lokaciju klijenta (pre same vožnje)
                                            if (tv.Position.X == currentTa.Request.From.X &&
                                                tv.Position.Y == currentTa.Request.From.Y)
                                            {
                                                int clientId;
                                                if (_clientIdByVehicleId.TryGetValue(tv.Id, out clientId))
                                                {
                                                    EndPoint cep;
                                                    if (_clientEpByClientId.TryGetValue(clientId, out cep))
                                                    {
                                                        var msg = Encoding.UTF8.GetBytes("Vehicle has arrived at your pickup location.");
                                                        try { _serverUdp.SendTo(msg, cep); } catch { }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                else if (vobj is RideStatusUpdate rs)
                                {
                                    // vozilo javlja završetak vožnje (km/cena)
                                    TaxiVehicle v;
                                    if (_activeVehicles.TryGetValue(rs.VehicleId, out v))
                                    {
                                        v.Kilometers += rs.Km;                 // Km je double/int
                                        v.Earnings += rs.Fare;         // Fare i Earnings su decimal
                                        v.PassengersServed += 1;
                                    }

                                    Console.WriteLine($"[Server] Ride finished: vehicle {rs.VehicleId}, client {rs.ClientId}, km={rs.Km:0}, fare={rs.Fare:0} RSD");
                                    PrintStatus();

                                    // Obavesti klijenta o završetku i očisti evidencije
                                    int clientId2;
                                    if (_clientIdByVehicleId.TryGetValue(rs.VehicleId, out clientId2))
                                    {
                                        EndPoint cep;
                                        if (_clientEpByClientId.TryGetValue(clientId2, out cep))
                                        {
                                            var finishMsg = Encoding.UTF8.GetBytes($"Arrived at destination. Fare: {rs.Fare:0} RSD.");
                                            try { _serverUdp.SendTo(finishMsg, cep); } catch { }
                                        }
                                        _clientEpByClientId.Remove(clientId2);
                                    }
                                    _clientIdByVehicleId.Remove(rs.VehicleId);
                                    _activeTasksByVehicleId.Remove(rs.VehicleId);
                                    _stepCounterByVehicleId.Remove(rs.VehicleId);
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
                                    _clientIdByVehicleId.Remove(removeId);
                                    _activeTasksByVehicleId.Remove(removeId);
                                    _stepCounterByVehicleId.Remove(removeId);
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
