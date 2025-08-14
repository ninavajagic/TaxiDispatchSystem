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
        // TCP (vozila) / UDP (klijenti)
        private const int VehicleTcpPort = 50000;
        private const int ClientUdpPort = 50001;

        // Parametar simulacije 
        private const double SpeedStepsPerSec = 0.8; // 1 "korak" na ~0.8 sek

        // Sockets
        private static Socket _serverTcp; // vozila (TCP)
        private static Socket _serverUdp; // klijenti (UDP)

        // Buffers
        private static readonly byte[] _bufVehicle = new byte[2048];
        private static readonly byte[] _bufClient = new byte[2048];

        // Evidencije
        private static readonly List<Socket> _vehicleSockets = new List<Socket>();              // otvorene TCP konekcije
        private static readonly Dictionary<int, TaxiVehicle> _activeVehicles = new Dictionary<int, TaxiVehicle>(); // id -> stanje vozila
        private static readonly Dictionary<int, Socket> _socketByVehicleId = new Dictionary<int, Socket>();    // id -> socket

        // Zadaci/klijenti
        private static readonly Dictionary<int, TaskAssignment> _taskByVehicleId = new Dictionary<int, TaskAssignment>(); // id vozila -> poslednji zadatak
        private static readonly HashSet<int> _activeClientIds = new HashSet<int>(); // "jedan aktivan zadatak po klijentu"

        // Obaveštavanje klijenata
        private static readonly Dictionary<int, int> _stepCounterByVehicleId = new Dictionary<int, int>(); // vozilo -> brojač koraka
        private static readonly Dictionary<int, EndPoint> _clientEpByClientId = new Dictionary<int, EndPoint>(); // klijent -> njegov UDP EP
        private static readonly Dictionary<int, int> _clientIdByVehicleId = new Dictionary<int, int>(); // vozilo -> klijent

        static void Main(string[] args)
        {
            Console.Title = "DispatchServer";

            // TCP (vozila)
            _serverTcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _serverTcp.Bind(new IPEndPoint(IPAddress.Any, VehicleTcpPort));
            _serverTcp.Blocking = false;
            _serverTcp.Listen(10);

            // UDP (klijenti)
            _serverUdp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _serverUdp.Bind(new IPEndPoint(IPAddress.Any, ClientUdpPort));
            _serverUdp.Blocking = false;

            Console.WriteLine($"[Server] TCP listening on {VehicleTcpPort} (vehicles)");
            Console.WriteLine($"[Server] UDP bound on {ClientUdpPort} (clients)");

            PrintStatusAndMap();

            while (true)
            {
                try
                {
                    var checkRead = new List<Socket>();
                    checkRead.Add(_serverTcp);
                    checkRead.Add(_serverUdp);
                    checkRead.AddRange(_vehicleSockets);

                    Socket.Select(checkRead, null, null, 1000);

                    foreach (var sock in checkRead)
                    {
                        if (sock == _serverTcp)
                        {
                            // nova TCP konekcija vozila
                            try
                            {
                                var vehicleSock = _serverTcp.Accept();
                                vehicleSock.Blocking = false;
                                _vehicleSockets.Add(vehicleSock);
                                Console.WriteLine($"[Server] Vehicle connected: {vehicleSock.RemoteEndPoint}");
                                PrintStatusAndMap();
                            }
                            catch (SocketException ex)
                            {
                                Console.WriteLine("[Server] Accept error: " + ex.Message);
                            }
                        }
                        else if (sock == _serverUdp)
                        {
                            // klijent šalje zahtev (UDP, BinaryFormatter)
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

                                if (obj is ClientRequest cr)
                                {
                                    Console.WriteLine($"[Server] UDP ClientRequest #{cr.ClientId}: from ({cr.From.X},{cr.From.Y}) to ({cr.To.X},{cr.To.Y})");

                                    // Guard: jedan aktivan zadatak po klijentu
                                    if (_activeClientIds.Contains(cr.ClientId))
                                    {
                                        SendUdpText("Request denied: you already have an active ride.", clientEp);
                                        continue;
                                    }

                                    // Nadji najbliže Available vozilo
                                    var best = FindNearestAvailable(_activeVehicles, cr.From);
                                    if (best == null)
                                    {
                                        SendUdpText("No vehicles available at the moment.", clientEp);
                                        continue;
                                    }

                                    // Formiraj TaskAssignment i pošalji vozilu (TCP)
                                    var ta = new TaskAssignment { VehicleId = best.Id, Request = cr };

                                    Socket vSock;
                                    if (_socketByVehicleId.TryGetValue(best.Id, out vSock))
                                    {
                                        using (var ms2 = new MemoryStream())
                                        {
                                            var bf2 = new BinaryFormatter();
                                            bf2.Serialize(ms2, ta);
                                            var payload = ms2.ToArray();
                                            vSock.Send(payload);
                                            Console.WriteLine($"[Server] TaskAssignment sent to vehicle {best.Id} ({payload.Length} bytes) for client {cr.ClientId}");
                                        }

                                        // upiši evidencije
                                        _taskByVehicleId[best.Id] = ta;
                                        _clientEpByClientId[cr.ClientId] = clientEp;
                                        _clientIdByVehicleId[best.Id] = cr.ClientId;
                                        _activeClientIds.Add(cr.ClientId);
                                        _stepCounterByVehicleId[best.Id] = 0;

                                        // ETA (distanca do klijenta / brzina)
                                        int d2c = Chebyshev(best.Position, cr.From);
                                        double etaSec = d2c / SpeedStepsPerSec;
                                        SendUdpText($"Vehicle {best.Id} is arriving in approx {etaSec:F1} seconds!", clientEp);
                                    }
                                    else
                                    {
                                        SendUdpText("Temporary error: vehicle socket not found.", clientEp);
                                    }
                                }
                                else
                                {
                                    // tekstualni UDP debug
                                    var txt = Encoding.UTF8.GetString(_bufClient, 0, read);
                                    Console.WriteLine($"[Server] UDP (text) from {clientEp}: {txt}");
                                    SendUdpText("ACK", clientEp);
                                }
                            }
                            catch (SocketException ex)
                            {
                                Console.WriteLine("[Server] UDP receive error: " + ex.Message);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("[Server] UDP deserialize error: " + ex.Message);
                            }
                        }
                        else
                        {
                            // poruka od povezanog vozila (TCP)
                            try
                            {
                                int read = sock.Receive(_bufVehicle);
                                if (read <= 0)
                                {
                                    HandleVehicleDisconnect(sock);
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
                                        _activeVehicles[tv.Id] = tv;
                                        _socketByVehicleId[tv.Id] = sock;

                                        Console.WriteLine($"[Server] Vehicle {tv.Id} @ ({tv.Position.X},{tv.Position.Y}) {tv.Status}");
                                        PrintStatusAndMap();

                                        // "approaching..." logika kad ide po klijenta
                                        if (tv.Status == RideStatus.GoingToPickup)
                                        {
                                            int clientId;
                                            if (_clientIdByVehicleId.TryGetValue(tv.Id, out clientId))
                                            {
                                                EndPoint cep;
                                                if (_clientEpByClientId.TryGetValue(clientId, out cep))
                                                {
                                                    if (!_stepCounterByVehicleId.ContainsKey(tv.Id))
                                                        _stepCounterByVehicleId[tv.Id] = 0;

                                                    _stepCounterByVehicleId[tv.Id]++;

                                                    TaskAssignment ta;
                                                    if (_taskByVehicleId.TryGetValue(tv.Id, out ta) && ta != null)
                                                    {
                                                        int d = Chebyshev(tv.Position, ta.Request.From);
                                                        if (_stepCounterByVehicleId[tv.Id] % 4 == 0 && d > 2)
                                                        {
                                                            double eta = d / SpeedStepsPerSec;
                                                            SendUdpText($"Vehicle {tv.Id} is approaching... ETA {eta:F1} seconds.", cep);
                                                        }

                                                        // vozilo stiglo do klijenta (pre nego što status pređe u InRide)
                                                        if (d == 0 && tv.Status != RideStatus.InRide)
                                                        {
                                                            SendUdpText("Your vehicle is at your location!", cep);
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else if (obj is RideStatusUpdate rs)
                                    {
                                        TaxiVehicle v;
                                        if (_activeVehicles.TryGetValue(rs.VehicleId, out v))
                                        {
                                            // ažuriraj statistiku
                                            v.Kilometers += rs.Km;
                                            v.Earnings += rs.Fare;
                                            v.PassengersServed++;

                                            // obavesti klijenta o završetku i očisti evidencije
                                            EndPoint cep;
                                            if (_clientEpByClientId.TryGetValue(rs.ClientId, out cep))
                                            {
                                                SendUdpText($"Arrived at destination! Ride fare: {rs.Fare} RSD.", cep);
                                            }

                                            _clientEpByClientId.Remove(rs.ClientId);
                                            _stepCounterByVehicleId.Remove(rs.VehicleId);
                                            _clientIdByVehicleId.Remove(rs.VehicleId);
                                            _taskByVehicleId.Remove(rs.VehicleId);
                                            _activeClientIds.Remove(rs.ClientId);

                                            Console.WriteLine($"[Server] Ride finished: vehicle {rs.VehicleId}, client {rs.ClientId}, km={rs.Km}, fare={rs.Fare} RSD");
                                            PrintStatusAndMap();
                                        }
                                    }
                                    else
                                    {
                                        Console.WriteLine("[Server] Unknown object received from vehicle.");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine("[Server] Vehicle message error: " + ex.Message);
                                }
                            }
                            catch (SocketException)
                            {
                                HandleVehicleDisconnect(sock);
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

        private static void HandleVehicleDisconnect(Socket sock)
        {
            Console.WriteLine($"[Server] Vehicle disconnected: {sock.RemoteEndPoint}");

            // nađi ID vozila po socketu
            int removeId = -1;
            foreach (var kv in _socketByVehicleId)
            {
                if (kv.Value == sock) { removeId = kv.Key; break; }
            }

            if (removeId != -1)
            {
                // ako je imao aktivan zadatak → javi klijentu “ride canceled”
                TaskAssignment ta;
                if (_taskByVehicleId.TryGetValue(removeId, out ta) && ta != null)
                {
                    int clientId;
                    if (_clientIdByVehicleId.TryGetValue(removeId, out clientId))
                    {
                        EndPoint cep;
                        if (_clientEpByClientId.TryGetValue(clientId, out cep))
                        {
                            SendUdpText("Sorry, assigned vehicle disconnected. Ride canceled. Please send request again.", cep);
                        }
                        _clientEpByClientId.Remove(clientId);
                        _activeClientIds.Remove(clientId);
                    }

                    _taskByVehicleId.Remove(removeId);
                    _clientIdByVehicleId.Remove(removeId);
                    _stepCounterByVehicleId.Remove(removeId);
                }

                _socketByVehicleId.Remove(removeId);
                _activeVehicles.Remove(removeId);
            }

            _vehicleSockets.Remove(sock);
            try { sock.Close(); } catch { }

            PrintStatusAndMap();
        }

        private static void SendUdpText(string text, EndPoint ep)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _serverUdp.SendTo(bytes, ep);
        }

        private static int Chebyshev(Coordinate a, Coordinate b)
        {
            // metrika max(|dx|,|dy|)
            return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        private static TaxiVehicle FindNearestAvailable(Dictionary<int, TaxiVehicle> vehicles, Coordinate clientFrom)
        {
            TaxiVehicle best = null;
            int bestD = int.MaxValue;

            foreach (var v in vehicles.Values)
            {
                if (v.Status == RideStatus.Available)
                {
                    int d = Chebyshev(v.Position, clientFrom);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = v;
                    }
                }
            }
            return best;
        }

        private static void PrintStatusAndMap()
        {
            Console.WriteLine();
            Console.WriteLine("=== TAXI DISPATCH ===");
            Console.WriteLine($"Vehicles connected (sockets): {_vehicleSockets.Count}");
            foreach (var v in _activeVehicles.Values.OrderBy(v => v.Id))
            {
                Console.WriteLine($" - ID {v.Id}: {v.Status} @ ({v.Position.X},{v.Position.Y})  Km={v.Kilometers}  Earn={v.Earnings}  Psg={v.PassengersServed}");
            }
            Console.WriteLine();

            // ASCII mapa 20×20 (ili iz SimulationConfig)
            int size = SimulationConfig.GridSize; // očekujemo 20
            string[,] map = new string[size, size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    map[x, y] = ".";

            // prvo upiši vozila
            foreach (var v in _activeVehicles.Values)
            {
                if (v.Position.X >= 0 && v.Position.X < size && v.Position.Y >= 0 && v.Position.Y < size)
                {
                    map[v.Position.X, v.Position.Y] = "V" + v.Id.ToString();
                }
            }

            // zatim upiši klijente / V+K za aktivne zadatke
            foreach (var kv in _taskByVehicleId)
            {
                int vid = kv.Key;
                TaskAssignment ta = kv.Value;
                TaxiVehicle veh;
                if (_activeVehicles.TryGetValue(vid, out veh))
                {
                    bool atClient = veh.Position.X == ta.Request.From.X && veh.Position.Y == ta.Request.From.Y;
                    bool inRide = veh.Status == RideStatus.InRide;

                    if (inRide || atClient)
                    {
                        if (veh.Position.X >= 0 && veh.Position.X < size && veh.Position.Y >= 0 && veh.Position.Y < size)
                        {
                            map[veh.Position.X, veh.Position.Y] = $"V{veh.Id}+K";
                        }
                    }
                    else
                    {
                        // klijent čeka na From
                        if (ta.Request.From.X >= 0 && ta.Request.From.X < size && ta.Request.From.Y >= 0 && ta.Request.From.Y < size)
                        {
                            map[ta.Request.From.X, ta.Request.From.Y] = "K" + ta.Request.ClientId.ToString();
                        }
                    }
                }
            }

            // ispis mape (y redovi, x kolone)
            Console.WriteLine("MAP (20x20):");
            for (int y = 0; y < size; y++)
            {
                var line = new StringBuilder();
                for (int x = 0; x < size; x++)
                {
                    line.Append(string.Format("{0,-6}", map[x, y]));
                }
                Console.WriteLine(line.ToString());
            }
            Console.WriteLine();
        }
    }
}
