using System.Collections.Generic;

namespace CatBattleground
{
    /// <summary>
    /// Made-up player names (2026-10-28, user: not the same every match). Everyone but yourself is shown under one of these, so a bot
    /// cannot be told from a person. The server picks a seed per match (MatchManager.NameSeed, replicated); every machine builds the
    /// same list from it, so all of them see the same name for the same anonymous player number, and nobody gets a duplicate.
    /// Names are a word + a word (e.g. "FluffyBiscuit"), some with a number; they avoid the cats' own names.
    /// </summary>
    public static class PlayerNames
    {
        static readonly string[] First =
        {
            "Fluffy", "Sneaky", "Tiny", "Mighty", "Lazy", "Spicy", "Sleepy", "Grumpy", "Happy", "Turbo", "Crazy", "Silent", "Lucky",
            "Fuzzy", "Salty", "Chonky", "Zoomy", "Sassy", "Brave", "Shadow", "Captain", "Major", "Baron", "Dr", "Little", "Big",
            "Cosmic", "Pixel", "Ginger", "Midnight", "Frosty", "Rusty", "Velvet", "Mister", "Madam", "Royal",
        };
        static readonly string[] Second =
        {
            "Paws", "Mittens", "Biscuit", "Mochi", "Nugget", "Pickles", "Noodle", "Pepper", "Tofu", "Socks", "Muffin", "Pudding",
            "Waffles", "Beans", "Toast", "Sushi", "Taco", "Boba", "Kitten", "Meow", "Purr", "Claws", "Tail", "Fang", "Pounce",
            "Furball", "Tuna", "Sardine", "Catnip", "Yarn", "Bell", "Button", "Cookie", "Dumpling", "Pretzel", "Marbles",
        };

        static int cachedSeed = int.MinValue;
        static readonly List<string> cache = new List<string>();

        /// <summary>The name of anonymous player <paramref name="number"/> (1..) in the match with this seed.</summary>
        public static string Of(int number, int seed)
        {
            if (number <= 0) return "???";
            if (seed != cachedSeed) Build(seed);
            while (cache.Count < number) Grow();
            return cache[number - 1];
        }

        static System.Random rng;

        static void Build(int seed)
        {
            cachedSeed = seed;
            cache.Clear();
            rng = new System.Random(seed);
            for (int i = 0; i < 16; i++) Grow();
        }

        static void Grow()
        {
            for (int tries = 0; tries < 50; tries++)
            {
                var name = First[rng.Next(First.Length)] + Second[rng.Next(Second.Length)];
                if (rng.NextDouble() < 0.3) name += rng.Next(2, 100).ToString();
                if (cache.Contains(name)) continue;
                cache.Add(name);
                return;
            }
            cache.Add("Cat" + (cache.Count + 1));
        }
    }
}
