using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>Stand-in for the blockchain: a fixed set of cards that every test wallet "owns".</summary>
    public static class MockChain
    {
        public static readonly PassCard[] Cards =
        {
            new PassCard { tokenId = "101", tier = CharacterTier.Common },
            new PassCard { tokenId = "102", tier = CharacterTier.Common },
            new PassCard { tokenId = "201", tier = CharacterTier.Rare },
            new PassCard { tokenId = "301", tier = CharacterTier.Legendary },
            new PassCard { tokenId = "302", tier = CharacterTier.Legendary },
        };

        public static bool TryGet(string tokenId, out PassCard card)
        {
            foreach (var c in Cards)
                if (c.tokenId == tokenId) { card = c; return true; }
            card = default;
            return false;
        }
    }

    public class MockWalletCardsProvider : IWalletCardsProvider
    {
        public void GetCards(string wallet, Action<List<PassCard>> done) => done(new List<PassCard>(MockChain.Cards));
    }

    /// <summary>
    /// Server-side mock: "ownership" comes from MockChain, the reveal is a cryptographically random pick among the
    /// characters of the card's tier, stored in a JSON file so it survives restarts (once per card, permanent).
    /// The real service will do the same against the Robinhood Chain contract and a backend.
    /// </summary>
    public class MockCardService : ICardService
    {
        [Serializable] class Reveal { public string tokenId; public string characterId; }
        [Serializable] class RevealFile { public List<Reveal> reveals = new List<Reveal>(); }

        readonly CharacterCatalog catalog;
        readonly string path;
        readonly Dictionary<string, string> revealed = new Dictionary<string, string>();

        public MockCardService(CharacterCatalog catalog)
        {
            this.catalog = catalog;
            path = Path.Combine(Application.persistentDataPath, "mock_card_reveals.json");
            Load();
        }

        /// <summary>Forget every reveal (testing only).</summary>
        public void ResetReveals()
        {
            revealed.Clear();
            Save();
        }

        public void Resolve(string wallet, string tokenId, Action<CardResolveResult> done)
        {
            if (catalog == null) { done(Fail("No character catalog assigned.")); return; }

            if (string.IsNullOrEmpty(tokenId))
            {
                var original = catalog.Original;
                done(original == null
                    ? Fail("Catalog has no Original character.")
                    : new CardResolveResult { ok = true, tokenId = "", tier = CharacterTier.Original, characterId = original.id });
                return;
            }

            if (!MockChain.TryGet(tokenId, out var card)) { done(Fail("Card #" + tokenId + " is not owned by this wallet.")); return; }

            bool isNew = false;
            if (!revealed.TryGetValue(tokenId, out var characterId) || catalog.Get(characterId) == null)
            {
                var pool = catalog.InTier(card.tier);
                if (pool.Count == 0) { done(Fail("No characters configured for tier " + card.tier + ".")); return; }
                characterId = pool[RandomNumberGenerator.GetInt32(pool.Count)].id;
                revealed[tokenId] = characterId;
                isNew = true;
                Save();
            }
            done(new CardResolveResult { ok = true, tokenId = tokenId, tier = card.tier, characterId = characterId, newlyRevealed = isNew });
        }

        static CardResolveResult Fail(string error) => new CardResolveResult { ok = false, error = error };

        void Load()
        {
            try
            {
                if (!File.Exists(path)) return;
                var file = JsonUtility.FromJson<RevealFile>(File.ReadAllText(path));
                if (file != null && file.reveals != null)
                    foreach (var r in file.reveals) revealed[r.tokenId] = r.characterId;
            }
            catch (Exception e) { Debug.LogWarning("MockCardService: could not read " + path + ": " + e.Message); }
        }

        void Save()
        {
            try
            {
                var file = new RevealFile();
                foreach (var kv in revealed) file.reveals.Add(new Reveal { tokenId = kv.Key, characterId = kv.Value });
                File.WriteAllText(path, JsonUtility.ToJson(file, true));
            }
            catch (Exception e) { Debug.LogWarning("MockCardService: could not write " + path + ": " + e.Message); }
        }
    }
}
