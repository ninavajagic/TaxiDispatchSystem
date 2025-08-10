using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SharedClasses.Models
{
    [Serializable]

    public class TaskAssignment
    {
        public int VehicleId { get; set; }  // ID vozila kome je dodeljen zadatak
        public ClientRequest Request { get; set; } = new ClientRequest(); // Zahtev klijenta koji se dodeljuje vozilu


    }
}
