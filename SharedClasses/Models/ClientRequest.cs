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
        public Coordinate Start { get; set; } = new Coordinate(); //koordinate polazne lokacije klijenta
        public Coordinate Destination { get; set; } = new Coordinate(); //koordinate odredi[ta klijenta

    }
}
