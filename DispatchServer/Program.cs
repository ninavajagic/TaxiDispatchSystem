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
        // === Konstante i parametri simulacije ===
        private const int GridSize = 20;                 // 20x20 mapa
        private const int VehicleTcpPort = 50000;        // TCP (vozila)
        private const int ClientUdpPort = 50001;         // UDP (klijenti)
        private const double SpeedStepsPerSec = 0.8;     // brzina: 1 korak / 0.8s (kao u referenci)

        // === Sockets ===
        private static Socket _serverTcp; // vehicles
        private static Socket _serverUdp; // clients

        // === Buffers ===
        private static readonly byte[] _bufVehicle = new byte[1024];
        private static readonly byte[] _bufClient = new byte[1024];

        // === Evidencija vozila/soketa ===
        private static readonly List<Socket> _vehicleSockets = new List<Socket>();
        private static readonly Dictionary<int, TaxiVehicle> _activeVehicles = new Dictionary<int, TaxiVehicle>(); // ID -> vozilo snapshot
        private static readonly Dictionary<int, Socket> _socketByVehicleId = new Dictionary<int, Socket>();         // ID -> socket

        // === Evidencija zadataka/klijenata (potrebno za mapu i guard) ===
        private static readonly Dictionary<int, TaskAssignment> _taskByClientId = new Dictionary<int, TaskAssignment>(); // 1 akt. zadatak po klijentu
        private static readonly Dictionary<int, TaskAssignment> _taskByVehicleId = new Dictionary<int, TaskAssignment>(); // koji zadatak vozi koje vozilo

        // === Za “approaching...” obaveštenja (kao u referenci) ===
        private static readonly Dictionary<int, int> _stepCounterByVehicleId = new Dictionary<int, int>(); // brojac koraka
        private static readonly Dictionary<int, EndPoint> _clientEpByClientId = new Dictionary<int, EndPoint>(); // UDP EP klijenta

        static void Main(string[] args)
        {
            Console.Title = "DispatchServer";

            // TCP server (vozila)
            _serverTcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _serverTcp.Bind(new IPEndPoint(IPAddress.Any, VehicleTcpPort));
            _serverTcp.Blocking = false;
            _serverTcp.Listen(10);

            // UDP server (klijenti)
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
                            // Klijent šalje ClientRequest (UDP, BinaryFormatter)
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

                                    // GUARD: jedan aktivan zadatak po klijentu
                                    if (_taskByClientId.ContainsKey(cr.ClientId))
                                    {
                                        SendUdpText("Request denied: you already have an active ride.", clientEp);
                                        continue;
                                    }

                                    // Nađi najbliže "Available" vozilo
                                    var best = FindNearestAvailable(_activeVehicles, cr.From);
                                    if (best == null)
                                    {
                                        SendUdpText("No vehicles available at the moment.", clientEp);
                                        continue;
                                    }

                                    // Formiraj zadatak i pošalji ga izabranom vozilu (TCP)
                                    var task = new TaskAssignment
                                    {
                                        VehicleId = best.Id,
                                        Request = cr
                                    };

                                    Socket vSock;
                                    if (_socketByVehicleId.TryGetValue(best.Id, out vSock))
                                    {
                                        using (var ms2 = new MemoryStream())
                                        {
                                            var bf2 = new BinaryFormatter();
                                            bf2.Serialize(ms2, task);
                                            var payload = ms2.ToArray();
                                            vSock.Send(payload);
                                            Console.WriteLine($"[Server] TaskAssignment sent to vehicle {best.Id} ({payload.Length} bytes) for client {cr.ClientId}");
                                        }

                                        // Sačuvaj evidenciju zadatka i EP klijenta
                                        _taskByClientId[cr.ClientId] = task;
                                        _taskByVehicleId[best.Id] = task;
                                        _clientEpByClientId[cr.ClientId] = clientEp;
                                        _stepCounterByVehicleId[best.Id] = 0;

                                        // Inicijalni ETA ka klijentu
                                        int distToClient = Chebyshev(best.Position, cr.From);
                                        double etaSec = distToClient / SpeedStepsPerSec;
                                        SendUdpText($"Vehicle {best.Id} is arriving in approx {etaSec:F1} seconds!", clientEp);

                                        PrintStatus();
                                    }
                                }
                                else
                                {
                                    // Debug tekstualna poruka (nije ClientRequest)
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
                            // Poruka od postojećeg vozila (TCP)
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
                                        // Ažuriraj stanje vozila + mapiranje soketa
                                        _activeVehicles[tv.Id] = tv;
                                        _socketByVehicleId[tv.Id] = sock;

                                        Console.WriteLine($"[Server] Vehicle {tv.Id} @ ({tv.Position.X},{tv.Position.Y}) {tv.Status}");

                                        // Ako ide ka klijentu → “approaching...” + eventualno “at your location!”
                                        TaskAssignment ta;
                                        if (tv.Status == RideStatus.GoingToPickup && _taskByVehicleId.TryGetValue(tv.Id, out ta))
                                        {
                                            // brojač koraka
                                            if (!_stepCounterByVehicleId.ContainsKey(tv.Id))
                                                _stepCounterByVehicleId[tv.Id] = 0;
                                            _stepCounterByVehicleId[tv.Id]++;

                                            int d = Chebyshev(tv.Position, ta.Request.From);
                                            EndPoint cep;
                                            if (_clientEpByClientId.TryGetValue(ta.Request.ClientId, out cep))
                                            {
                                                if (_stepCounterByVehicleId[tv.Id] % 4 == 0 && d > 2)
                                                {
                                                    double eta = d / SpeedStepsPerSec;
                                                    SendUdpText($"Vehicle {tv.Id} is approaching... ETA {eta:F1} seconds.", cep);
                                                }

                                                if (d == 0) // stigao do klijenta (pre prelaska u InRide)
                                                {
                                                    SendUdpText("Your vehicle is at your location!", cep);
                                                }
                                            }
                                        }

                                        PrintStatus();
                                    }
                                    else if (obj is RideStatusUpdate rs)
                                    {
                                        // Kraj vožnje – ažuriraj statistiku, obavesti klijenta, očisti evidenciju
                                        TaxiVehicle vSnap;
                                        if (_activeVehicles.TryGetValue(rs.VehicleId, out vSnap))
                                        {
                                            vSnap.Kilometers += rs.Km;
                                            vSnap.Earnings += rs.Fare;
                                            vSnap.PassengersServed++;

                                            EndPoint cep;
                                            if (_clientEpByClientId.TryGetValue(rs.ClientId, out cep))
                                            {
                                                SendUdpText($"Arrived at destination! Ride fare: {rs.Fare} RSD.", cep);
                                            }

                                            // očisti task mape
                                            _taskByVehicleId.Remove(rs.VehicleId);
                                            _taskByClientId.Remove(rs.ClientId);
                                            _stepCounterByVehicleId.Remove(rs.VehicleId);

                                            Console.WriteLine($"[Server] Ride finished: vehicle {rs.VehicleId}, client {rs.ClientId}, km={rs.Km}, fare={rs.Fare} RSD");
                                            PrintStatus();
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

        // === Pomoćne funkcije ===

        private static void HandleVehicleDisconnect(Socket sock)
        {
            Console.WriteLine($"[Server] Vehicle disconnected: {sock.RemoteEndPoint}");

            int removeId = -1;
            foreach (var kv in _socketByVehicleId)
            {
                if (kv.Value == sock) { removeId = kv.Key; break; }
            }
            if (removeId != -1)
            {
                _socketByVehicleId.Remove(removeId);
                _activeVehicles.Remove(removeId);

                // ako je vozilo imalo zadatak – očisti i njega (klijent i brojač)
                TaskAssignment ta;
                if (_taskByVehicleId.TryGetValue(removeId, out ta))
                {
                    _taskByVehicleId.Remove(removeId);
                    _taskByClientId.Remove(ta.Request.ClientId);
                    _stepCounterByVehicleId.Remove(removeId);
                }
            }

            _vehicleSockets.Remove(sock);
            try { sock.Close(); } catch { }

            PrintStatus();
        }

        private static void SendUdpText(string text, EndPoint ep)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _serverUdp.SendTo(bytes, ep);
        }

        private static int Chebyshev(Coordinate a, Coordinate b)
        {
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

        // Ispis kompletne slike stanja: zaglavlje, tabela vozila, aktivni zadaci i 20x20 mapa
        private static void PrintStatus()
        {
            Console.Clear();
            Console.WriteLine($"[Server] TCP listening on {VehicleTcpPort} (vehicles)");
            Console.WriteLine($"[Server] UDP bound on {ClientUdpPort} (clients)");
            Console.WriteLine();
            Console.WriteLine("=== TAXI DISPATCH ===");
            Console.WriteLine($"Vehicles connected (sockets): {_vehicleSockets.Count}");
            Console.WriteLine();

            PrintVehicleTable();
            PrintActiveTasks();
            PrintMap();

            Console.WriteLine(); // malo prostora
        }

        private static void PrintVehicleTable()
        {
            Console.WriteLine("ID  Status          Position   Km   Earn     Psg");
            Console.WriteLine("--  --------------  --------  ----  --------  ---");
            foreach (var v in _activeVehicles.Values.OrderBy(v => v.Id))
            {
                Console.WriteLine(
                    $"{v.Id,2}  {v.Status,-14}  ({v.Position.X,2},{v.Position.Y,2})  {v.Kilometers,4}  {v.Earnings,8}  {v.PassengersServed,3}");
            }
            Console.WriteLine();
        }

        private static void PrintActiveTasks()
        {
            if (_taskByClientId.Count == 0)
            {
                Console.WriteLine("Active tasks: none");
                Console.WriteLine();
                return;
            }

            Console.WriteLine("Active tasks:");
            foreach (var ta in _taskByClientId.Values.OrderBy(t => t.Request.ClientId))
            {
                Console.WriteLine($" - Client {ta.Request.ClientId} → Vehicle {ta.VehicleId} : " +
                                  $"from ({ta.Request.From.X},{ta.Request.From.Y}) to ({ta.Request.To.X},{ta.Request.To.Y})");
            }
            Console.WriteLine();
        }

        private static void PrintMap()
        {
            // priprema prazne mape
            string[,] map = new string[GridSize, GridSize];
            for (int y = 0; y < GridSize; y++)
                for (int x = 0; x < GridSize; x++)
                    map[x, y] = ".";

            // nacrtaj vozila
            foreach (var v in _activeVehicles.Values)
            {
                if (v.Position.X >= 0 && v.Position.X < GridSize &&
                    v.Position.Y >= 0 && v.Position.Y < GridSize)
                {
                    map[v.Position.X, v.Position.Y] = "V" + v.Id;
                }
            }

            // klijenti / V+K
            foreach (var ta in _taskByClientId.Values)
            {
                TaxiVehicle vNow;
                _activeVehicles.TryGetValue(ta.VehicleId, out vNow);

                if (vNow != null && vNow.Status == RideStatus.InRide)
                {
                    // pokaži V+K na poziciji vozila
                    if (vNow.Position.X >= 0 && vNow.Position.X < GridSize &&
                        vNow.Position.Y >= 0 && vNow.Position.Y < GridSize)
                    {
                        map[vNow.Position.X, vNow.Position.Y] = "V" + vNow.Id + "+K";
                    }
                }
                else
                {
                    // klijent čeka na startnoj tački
                    var c = ta.Request.From;
                    if (c.X >= 0 && c.X < GridSize && c.Y >= 0 && c.Y < GridSize)
                    {
                        // ako već stoji "Vx" na toj ćeliji – prikaži Vx+K
                        var cur = map[c.X, c.Y];
                        if (cur.StartsWith("V"))
                            map[c.X, c.Y] = cur + "+K";
                        else
                            map[c.X, c.Y] = "K" + ta.Request.ClientId;
                    }
                }
            }

            Console.WriteLine("MAP (20x20):");
            for (int y = 0; y < GridSize; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    Console.Write($"{map[x, y],-6}");
                }
                Console.WriteLine();
            }
        }
    }
}
