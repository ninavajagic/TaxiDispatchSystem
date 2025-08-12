using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

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
                            //Klijent salje 
                            try
                            {
                                EndPoint clientEp = new IPEndPoint(IPAddress.Any, 0);
                                int read = _serverUdp.ReceiveFrom(_bufClient, ref clientEp);
                                string msg = Encoding.UTF8.GetString(_bufClient, 0, read);
                                Console.WriteLine($"[Server] UDP from {clientEp}: {msg}");

                                //Minimalni odgovor
                                var reply = Encoding.UTF8.GetBytes("ACK");
                                _serverUdp.SendTo(reply, clientEp);
                            }
                            catch (SocketException ex)
                            {
                                Console.WriteLine("[Server] UDP recv error: " + ex.Message);
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
                                    _vehicleSockets.Remove(sock);
                                    sock.Close();
                                    PrintStatus();
                                    continue;
                                }
                                string msg = Encoding.UTF8.GetString(_bufVehicle, 0, read);
                                Console.WriteLine($"[Server] TCP from {sock.RemoteEndPoint}: {msg}");
                            }
                            catch (SocketException)
                            {
                                //force close
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
            Console.WriteLine($"Vehicles connected: {_vehicleSockets.Count}");
            Console.WriteLine();
        }
    }
}
