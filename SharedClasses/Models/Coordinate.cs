using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SharedClasses.Models
{
    [Serializable]
    public class Coordinate
    {
        public int X { get; set; }
        public int Y { get; set; }   
        
        public Coordinate() { }

        public Coordinate(int x, int y)
        {
            X = x;
            Y = y;
        }
    }
}
