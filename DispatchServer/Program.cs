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
        // TCP (vehicles) / UDP (clients)
        private const int VehicleTcpPort = 50000;
        private const int ClientUdpPort = 50001;

        // Parametri simulacije: u referentnom projektu koriste "brzina" = 0.8 koraka/sek
        // ETA računamo kao vreme = dist / 0.8
        private const double SpeedStepsPerSec = 0.8;

        // Sockets
        private static Socket _serverTcp; // vozila (TCP)
        private static Socket _serverUdp; // klijenti (UDP)

        // Buffers
        private static readonly byte[] _bufVehicle = new byte[1024];
        private static readonly byte[] _bufClient = new byte[1024];

        // Evidencije aktivnih konekcija i stanja
        private static readonly List<Socket> _vehicleSockets = new List<Socket>();                // aktivni TCP soketi vozila
        private static readonly Dictionary<int, TaxiVehicle> _activeVehicles = new Dictionary<int, TaxiVehicle>(); // ID -> stanje vozila
        private static readonly Dictionary<int, Socket> _socketByVehicleId = new Dictionary<int, Socket>();        // ID -> TCP socket

        // Obaveštavanje klijenata (po uzoru na referentni kod)
        private static readonly Dictionary<int, int> _stepCounterByVehicleId = new Dictionary<int, int>(); // "brojacKorakaPoZadatku"
        private static readonly Dictionary<int, EndPoint> _clientEpByClientId = new Dictionary<int, EndPoint>();    // "EPPoIDKlijenta"
        private static readonly Dictionary<int, int> _clientIdByVehicleId = new Dictionary<int, int>();             // "VoziloKlijentID"

        // NOVO: Pamtimo poslednji TaskAssignment po vozilu da bismo imali From/To koordinate za "approaching/arrival" poruke
        private static readonly Dictionary<int, TaskAssignment> _taskByVehicleId = new Dictionary<int, TaskAssignment>();

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

                    // timeout ~1ms
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
                            // Klijent šalje zahtev (UDP, BinaryFormatter)
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

                                    // Ako klijent već ima aktivnu vožnju — odbij zahtev
                                    bool alreadyActive = _clientIdByVehicleId.Values.Contains(cr.ClientId);
                                    if (alreadyActive)
                                    {
                                        SendUdpText("Request denied: you already have an active ride.", clientEp);
                                        continue;
                                    }

                                    // Nađi najbliže dostupno vozilo
                                    var best = FindNearestAvailable(_activeVehicles, cr.From);
                                    if (best == null)
                                    {
                                        SendUdpText("No vehicles available at the moment.", clientEp);
                                        continue;
                                    }

                                    // Formiraj zadatak i pošalji vozilu (TCP)
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

                                        // Zapamti poslednji task za to vozilo (za approaching/arrival poruke)
                                        _taskByVehicleId[best.Id] = task;

                                        // Pošalji inicijalni ETA klijentu (vozilo -> klijent)
                                        int distToClient = Chebyshev(best.Position, cr.From);
                                        double etaSec = distToClient / SpeedStepsPerSec;
                                        SendUdpText($"Vehicle {best.Id} is arriving in approx {etaSec:F1} seconds!", clientEp);

                                        // Evidencija za kasnija obaveštavanja preko UDP-a
                                        _stepCounterByVehicleId[best.Id] = 0;
                                        _clientEpByClientId[cr.ClientId] = clientEp;
                                        _clientIdByVehicleId[best.Id] = cr.ClientId;
                                    }
                                }
                                else
                                {
                                    // Tekstualni UDP - debug
                                    var txt = Encoding.UTF8.GetString(_bufClient, 0, read);
                                    Console.WriteLine($"[Server] UDP (text) from {clientEp}: {txt}");
                                    SendUdpText("ACK", clientEp);
                                }
                            }
                            catch (SocketException ex)
                            {
                                // npr. "forcibly closed by remote host" kada klijent zatvori socket
                                Console.WriteLine("[Server] UDP receive error: " + ex.Message);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("[Server] UDP deserialize error: " + ex.Message);
                            }
                        }
                        else
                        {
                            // TCP poruka od povezanog vozila
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
                                        // Ažuriraj stanje vozila + mapiraj socket
                                        _activeVehicles[tv.Id] = tv;
                                        _socketByVehicleId[tv.Id] = sock;

                                        Console.WriteLine($"[Server] Vehicle {tv.Id} @ ({tv.Position.X},{tv.Position.Y}) {tv.Status}");
                                        PrintStatus();

                                        // Ako vozilo ide ka klijentu, periodično pošalji "approaching..." i javi kad stigne
                                        if (tv.Status == RideStatus.GoingToPickup
                                            && _clientIdByVehicleId.ContainsKey(tv.Id)
                                            && _taskByVehicleId.ContainsKey(tv.Id))
                                        {
                                            int cId = _clientIdByVehicleId[tv.Id];
                                            EndPoint cep;
                                            if (_clientEpByClientId.TryGetValue(cId, out cep))
                                            {
                                                if (!_stepCounterByVehicleId.ContainsKey(tv.Id))
                                                    _stepCounterByVehicleId[tv.Id] = 0;

                                                _stepCounterByVehicleId[tv.Id]++;

                                                TaskAssignment ta;
                                                if (_taskByVehicleId.TryGetValue(tv.Id, out ta) && ta != null && ta.Request != null)
                                                {
                                                    int d = Chebyshev(tv.Position, ta.Request.From);

                                                    // na svaka 4 koraka, dok je > 2 polja daleko
                                                    if (_stepCounterByVehicleId[tv.Id] % 4 == 0 && d > 2)
                                                    {
                                                        double eta = d / SpeedStepsPerSec;
                                                        SendUdpText($"Vehicle {tv.Id} is approaching... ETA {eta:F1} seconds.", cep);
                                                    }

                                                    // upravo stigao na lokaciju klijenta (pre prelaska u InRide)
                                                    if (d == 0 && tv.Status != RideStatus.InRide)
                                                    {
                                                        SendUdpText("Your vehicle is at your location!", cep);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else if (obj is RideStatusUpdate rs)
                                    {
                                        // Završetak vožnje (statistika + poruka klijentu + čišćenje evidencija)
                                        TaxiVehicle v;
                                        if (_activeVehicles.TryGetValue(rs.VehicleId, out v))
                                        {
                                            v.Kilometers += rs.Km;
                                            v.Earnings += rs.Fare;
                                            v.PassengersServed += 1;

                                            EndPoint cep;
                                            if (_clientEpByClientId.TryGetValue(rs.ClientId, out cep))
                                            {
                                                SendUdpText($"Arrived at destination! Ride fare: {rs.Fare} RSD.", cep);
                                            }

                                            // očisti sve evidencije za taj task/vozilo
                                            _clientIdByVehicleId.Remove(rs.VehicleId);
                                            _stepCounterByVehicleId.Remove(rs.VehicleId);
                                            _taskByVehicleId.Remove(rs.VehicleId);
                                            _clientEpByClientId.Remove(rs.ClientId);

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

        // Detektuj i očisti sve evidencije kad vozilo nestane
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
                _stepCounterByVehicleId.Remove(removeId);
                _taskByVehicleId.Remove(removeId);

                int cId;
                if (_clientIdByVehicleId.TryGetValue(removeId, out cId))
                {
                    _clientIdByVehicleId.Remove(removeId);
                    // _clientEpByClientId.Remove(cId); // može i da se zadrži; klijent možda pošalje novi zahtev
                }
            }

            _vehicleSockets.Remove(sock);
            try { sock.Close(); } catch { }

            PrintStatus();
        }

        // Jednostavan UDP tekstualni odgovor
        private static void SendUdpText(string text, EndPoint ep)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _serverUdp.SendTo(bytes, ep);
        }

        // Chebyshev distanca (max od |dx|, |dy|): po uzoru na referentni projekat
        private static int Chebyshev(Coordinate a, Coordinate b)
        {
            int dx = Math.Abs(a.X - b.X);
            int dy = Math.Abs(a.Y - b.Y);
            return dx > dy ? dx : dy;
        }

        // Nađi najbliže slobodno vozilo (Status == Available)
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

        // Pregled stanja na konzoli
        private static void PrintStatus()
        {
            Console.WriteLine();
            Console.WriteLine("=== TAXI DISPATCH ===");
            Console.WriteLine($"Vehicles connected (sockets): {_vehicleSockets.Count}");
            foreach (var v in _activeVehicles.Values.OrderBy(v => v.Id))
            {
                Console.WriteLine($" - ID {v.Id}: {v.Status} @ ({v.Position.X},{v.Position.Y})  Km={v.Kilometers}  Earn={v.Earnings}  Psg={v.PassengersServed}");
            }
            Console.WriteLine();
        }
    }
}
