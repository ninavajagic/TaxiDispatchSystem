using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SharedClasses.Models
{
    [Serializable]

    public class ClientRequest
    {
        public int ClientId { get; set; } // ID klijenta
        public Coordinate From { get; set; } = new Coordinate(); // početna lokacija
        public Coordinate To { get; set; } = new Coordinate(); // odredište

    }
}
