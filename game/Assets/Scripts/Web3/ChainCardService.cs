using System;
using System.Collections.Generic;

namespace CatBattleground
{
    /// <summary>
    /// Real pass cards: ownership and the revealed cat come from CMBGPassCard on Robinhood Chain (fresh read at every
    /// match start). A sealed card is not playable: it has to be opened on the website first (the reveal lives on-chain).
    /// NOTE: the wallet address comes from the client and is not signature-checked yet. Fine for the offline beta (the
    /// browser hosts its own match); an online server / prize leaderboard must verify a wallet signature first.
    /// </summary>
    public class ChainCardService : ICardService, IWalletCardsProvider
    {
        readonly CharacterCatalog catalog;

        public ChainCardService(CharacterCatalog catalog) { this.catalog = catalog; }

        public void GetCards(string wallet, Action<List<PassCard>> done) =>
            ChainCards.Fetch(wallet, (cards, error) => done(cards ?? new List<PassCard>()));

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
            if (!ChainCards.IsAddress(wallet)) { done(Fail("Connect your wallet to use card #" + tokenId + ".")); return; }

            ChainCards.Fetch(wallet, (cards, error) =>
            {
                if (cards == null) { done(Fail(error ?? "Could not read the cards.")); return; }
                var card = cards.Find(c => c.tokenId == tokenId);
                if (card.tokenId == null) { done(Fail("Card #" + tokenId + " is not in this wallet any more.")); return; }
                if (!card.Playable) { done(Fail("Card #" + tokenId + " is still sealed. Open it on the website first.")); return; }
                var entry = catalog.Get(card.characterId);
                if (entry == null || entry.playerPrefab == null) { done(Fail("This game version does not know the cat '" + card.characterId + "' yet.")); return; }
                done(new CardResolveResult { ok = true, tokenId = card.tokenId, tier = card.tier, characterId = card.characterId, newlyRevealed = false });
            });
        }

        static CardResolveResult Fail(string error) => new CardResolveResult { ok = false, error = error };
    }
}
