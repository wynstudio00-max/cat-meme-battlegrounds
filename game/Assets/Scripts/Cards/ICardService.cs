using System;
using System.Collections.Generic;

namespace CatBattleground
{
    /// <summary>
    /// Server side. Decides which character a wallet gets for a card: checks ownership, and on the first use of a card
    /// draws one of the characters in its tier and remembers it forever. Callback style (no Task) so a
    /// UnityWebRequest-based implementation can replace the mock without changing callers.
    /// </summary>
    public interface ICardService
    {
        void Resolve(string wallet, string tokenId, Action<CardResolveResult> done);
    }

    /// <summary>Client side. Lists the pass cards the connected wallet owns (for the card picker).</summary>
    public interface IWalletCardsProvider
    {
        void GetCards(string wallet, Action<List<PassCard>> done);
    }
}
