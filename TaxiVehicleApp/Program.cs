using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace TaxiVehicleApp
{
    class Program
    {
        private const string ServerHost = "127.0.0.1";
        private const int ServerPort = 50000;
        static void Main(string[] args)
        {
            Console.Title = "TaxiVehicleApp";
            Console.Write("Enter Vehicle Id: ");
            int id = int.TryParse(Console.ReadLine(), out var v) ? v : 1;

            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect(ServerHost, ServerPort);
                    Console.WriteLine($"[Vehicle {id}] Connected to server {ServerHost}:{ServerPort}");

                    var stream = client.GetStream();
                    var hello = Encoding.UTF8.GetBytes($"HELLO:{id}");
                    stream.Write(hello, 0, hello.Length);
                    stream.Flush();

                    Console.WriteLine("[Vehicle] Press ENTER to exit...");
                    Console.ReadLine();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Vehicle] Error: " + ex.Message);
                Console.ReadLine();
            }
        }
    }
}
