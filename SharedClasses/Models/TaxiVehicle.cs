using SharedClasses.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SharedClasses.Models
{
    [Serializable]
    public class TaxiVehicle
    {
        public int Id { get; set; } // jedinstveni identifikator vozila
        public Coordinate Position { get; set; } = new Coordinate();  // trenutne koordinate vozila na mapi
        public RideStatus Status { get; set; } = RideStatus.Available; // trenutni status vozila

        public double Kilometers { get; set; }  // ukupna pređena kilometraža
        public decimal Earnings { get; set; }  // zarada
        public int PassengersServed { get; set; } // ukupan broj prevezenih putnika
    }
}
