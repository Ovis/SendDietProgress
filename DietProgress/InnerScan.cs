using System;
using System.Collections.Generic;

namespace DietProgress
{
    class InnerScan
    {
        public class Health
        {
            public String date { get; set; }
            public String keydata { get; set; }
            public String model { get; set; }
            public String tag { get; set; }
        }
        
        public String birth_date { get; set; }
        public List<Health> data { get; set; }
        public String height { get; set; }
        public String sex { get; set; }
    }
}
