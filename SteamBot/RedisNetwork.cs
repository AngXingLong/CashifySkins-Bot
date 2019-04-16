using StackExchange.Redis;

namespace SteamBot
{
    static class RedisNetwork
    {
        public static ConnectionMultiplexer Redis = ConnectionMultiplexer.Connect("localhost");

        public static void Reconnect()
        {
            Redis = ConnectionMultiplexer.Connect("localhost");
        }
    }
}
