using System;

namespace DietProgress
{
    class Token
    {
        public String access_token { get; set; }

        public int expires_in { get; set; }

        public String refresh_token { get; set; }
    }
}
