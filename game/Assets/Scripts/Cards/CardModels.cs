using System;

namespace CatBattleground
{
    /// <summary>A pass card owned by a wallet (what the NFT says: its id, tier and - once opened on the website - its cat).</summary>
    [Serializable]
    public struct PassCard
    {
        public string tokenId;
        public CharacterTier tier;
        public string characterId;   // catalog id of the revealed cat; empty while the card is sealed
        public bool opened;          // false = sealed (or still being revealed): open it on the website first

        public bool Playable => opened && !string.IsNullOrEmpty(characterId);
    }

    /// <summary>Outcome of validating a card for a session. characterId is the cat the player will get.</summary>
    public struct CardResolveResult
    {
        public bool ok;
        public string error;
        public string tokenId;          // empty = no card (original character)
        public CharacterTier tier;
        public string characterId;
        public bool newlyRevealed;      // true the first time this card is ever used
    }

    /// <summary>Sent by the client in the connection request. Later the session token will be a JWT issued after the wallet signature.</summary>
    [Serializable]
    public class ConnectPayload
    {
        public string wallet = "";
        public string tokenId = "";       // chosen card, empty = original
        public string sessionToken = "";
        public string testCharacter = ""; // catalog id; only honoured by an editor / development-build server (testing without cards)
    }
}
