using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SharedClasses.Models
{
    [Serializable]
    public class RideStatusUpdate
    {
        public int ClientId { get; set; }
        public int VehicleId { get; set; }
        public double Km { get; set; }       // koristimo "korake" kao km 
        public decimal Fare { get; set; }     // cena = Km * 80
    }
}
