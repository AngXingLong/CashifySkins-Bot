using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SteamBot
{
    // Acts as a simple object to submit a the bot manager that the bot is ready to shutdown
    static class WorkerToManager 
    {
        public static List<ulong> botsid = new List<ulong>();
    }
}
