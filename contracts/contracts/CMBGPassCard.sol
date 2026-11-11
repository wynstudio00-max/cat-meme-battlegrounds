// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {ERC721} from "@openzeppelin/contracts/token/ERC721/ERC721.sol";
import {ERC721Enumerable} from "@openzeppelin/contracts/token/ERC721/extensions/ERC721Enumerable.sol";
import {ERC2981} from "@openzeppelin/contracts/token/common/ERC2981.sol";
import {Ownable} from "@openzeppelin/contracts/access/Ownable.sol";
import {Ownable2Step} from "@openzeppelin/contracts/access/Ownable2Step.sol";
import {Pausable} from "@openzeppelin/contracts/utils/Pausable.sol";
import {MerkleProof} from "@openzeppelin/contracts/utils/cryptography/MerkleProof.sol";
import {Strings} from "@openzeppelin/contracts/utils/Strings.sol";

interface IRandomSource {
    function request(uint256 tokenId) external;
}

/// @title CMBG Pass Card (Cat Meme Battlegrounds)
/// @notice Sealed tier cards claimed from a CMBG balance snapshot (Merkle). Opening a card draws one character of its
///         tier once; the character is stored on-chain and travels with the card. See Contracts/SPEC.md.
contract CMBGPassCard is ERC721Enumerable, ERC2981, Ownable2Step, Pausable {
    using Strings for uint256;

    uint8 public constant COMMON = 1;
    uint8 public constant RARE = 2;
    uint8 public constant LEGENDARY = 3;
    uint8 public constant MAX_PER_WALLET = 2;

    enum State { None, Sealed, Opening, Opened }

    struct Card {
        uint8 tier;
        uint8 character; // 0 until opened
        State state;
    }

    bytes32 public merkleRoot;
    uint64 public claimDeadline; // unix seconds, 0 = open-ended
    address public randomSource;
    string private _base;
    bool public metadataFrozen;

    uint256 private _nextId = 1;
    mapping(uint256 => Card) private _cards;
    mapping(address => bool) public claimed;
    mapping(uint256 => address) public requestedFrom;
    mapping(uint8 => uint8[]) private _pool;

    event Claimed(address indexed account, uint8 commons, uint8 rares, uint8 legendaries);
    event OpenRequested(uint256 indexed tokenId, address indexed by, address source);
    event CardOpened(uint256 indexed tokenId, uint8 tier, uint8 character);
    event MerkleRootSet(bytes32 root);
    event ClaimDeadlineSet(uint64 deadline);
    event RandomSourceSet(address source);
    event MetadataFrozen();
    // ERC-4906
    event MetadataUpdate(uint256 _tokenId);
    event BatchMetadataUpdate(uint256 _fromTokenId, uint256 _toTokenId);

    error AlreadyClaimed();
    error BadAmount();
    error BadProof();
    error ClaimClosed();
    error NotCardOwner();
    error WrongState();
    error NoRandomSource();
    error NotRandomSource();
    error Frozen();

    constructor(address initialOwner, bytes32 root, string memory baseURI_)
        ERC721("CMBG Pass Card", "CMBGPASS")
        Ownable(initialOwner)
    {
        merkleRoot = root;
        _base = baseURI_;
        _pool[COMMON] = [1, 2, 4];    // gold, soldier, baby   (2026-11-07: baby <-> ninja swapped, ids kept)
        _pool[RARE] = [3, 5, 6];      // ninja, nobi, black
        _pool[LEGENDARY] = [7, 8];    // white, knight
    }

    // ---------------------------------------------------------------- claim

    function claim(uint8 commons, uint8 rares, uint8 legendaries, bytes32[] calldata proof) external whenNotPaused {
        if (claimDeadline != 0 && block.timestamp > claimDeadline) revert ClaimClosed();
        if (claimed[msg.sender]) revert AlreadyClaimed();
        uint256 total = uint256(commons) + rares + legendaries;
        if (total == 0 || total > MAX_PER_WALLET) revert BadAmount();
        bytes32 leaf = keccak256(bytes.concat(keccak256(abi.encode(msg.sender, commons, rares, legendaries))));
        if (!MerkleProof.verifyCalldata(proof, merkleRoot, leaf)) revert BadProof();

        claimed[msg.sender] = true;
        emit Claimed(msg.sender, commons, rares, legendaries);
        _mintTier(msg.sender, LEGENDARY, legendaries);
        _mintTier(msg.sender, RARE, rares);
        _mintTier(msg.sender, COMMON, commons);
    }

    function _mintTier(address to, uint8 tier, uint8 count) private {
        for (uint256 i; i < count; ++i) {
            uint256 id = _nextId++;
            _cards[id] = Card(tier, 0, State.Sealed);
            _safeMint(to, id);
        }
    }

    // ---------------------------------------------------------------- open

    function open(uint256 tokenId) external whenNotPaused {
        if (ownerOf(tokenId) != msg.sender) revert NotCardOwner();
        Card storage c = _cards[tokenId];
        if (c.state != State.Sealed) revert WrongState();
        c.state = State.Opening;
        _request(tokenId);
    }

    /// @notice If the random source was replaced while this card was waiting, ask the new one.
    function reRequest(uint256 tokenId) external whenNotPaused {
        if (ownerOf(tokenId) != msg.sender) revert NotCardOwner();
        if (_cards[tokenId].state != State.Opening || requestedFrom[tokenId] == randomSource) revert WrongState();
        _request(tokenId);
    }

    function _request(uint256 tokenId) private {
        address src = randomSource;
        if (src == address(0)) revert NoRandomSource();
        requestedFrom[tokenId] = src;
        emit OpenRequested(tokenId, msg.sender, src);
        IRandomSource(src).request(tokenId);
    }

    function fulfillRandomness(uint256 tokenId, uint256 word) external {
        if (msg.sender != randomSource || requestedFrom[tokenId] != msg.sender) revert NotRandomSource();
        Card storage c = _cards[tokenId];
        if (c.state != State.Opening) revert WrongState();
        uint8[] storage pool = _pool[c.tier];
        c.character = pool[word % pool.length];
        c.state = State.Opened;
        emit CardOpened(tokenId, c.tier, c.character);
        emit MetadataUpdate(tokenId);
    }

    // ---------------------------------------------------------------- views

    function cardOf(uint256 tokenId) external view returns (uint8 tier, uint8 character, State state) {
        _requireOwned(tokenId);
        Card memory c = _cards[tokenId];
        return (c.tier, c.character, c.state);
    }

    function characterOf(uint256 tokenId) external view returns (uint8) {
        _requireOwned(tokenId);
        return _cards[tokenId].character;
    }

    /// @notice Everything the game / website needs for one wallet in one call.
    function cardsOf(address account)
        external
        view
        returns (uint256[] memory ids, uint8[] memory tiers, uint8[] memory characters, State[] memory states)
    {
        uint256 n = balanceOf(account);
        ids = new uint256[](n);
        tiers = new uint8[](n);
        characters = new uint8[](n);
        states = new State[](n);
        for (uint256 i; i < n; ++i) {
            uint256 id = tokenOfOwnerByIndex(account, i);
            Card memory c = _cards[id];
            ids[i] = id;
            tiers[i] = c.tier;
            characters[i] = c.character;
            states[i] = c.state;
        }
    }

    function poolOf(uint8 tier) external view returns (uint8[] memory) {
        return _pool[tier];
    }

    function tokenURI(uint256 tokenId) public view override returns (string memory) {
        _requireOwned(tokenId);
        Card memory c = _cards[tokenId];
        if (c.state == State.Opened) {
            return string.concat(_base, "character/", uint256(c.character).toString(), ".json");
        }
        return string.concat(_base, "sealed/", uint256(c.tier).toString(), ".json");
    }

    // ---------------------------------------------------------------- admin

    function setMerkleRoot(bytes32 root) external onlyOwner {
        merkleRoot = root;
        emit MerkleRootSet(root);
    }

    function setClaimDeadline(uint64 deadline) external onlyOwner {
        claimDeadline = deadline;
        emit ClaimDeadlineSet(deadline);
    }

    function setRandomSource(address source) external onlyOwner {
        randomSource = source;
        emit RandomSourceSet(source);
    }

    function setBaseURI(string calldata baseURI_) external onlyOwner {
        if (metadataFrozen) revert Frozen();
        _base = baseURI_;
        if (_nextId > 1) emit BatchMetadataUpdate(1, _nextId - 1);
    }

    function freezeMetadata() external onlyOwner {
        metadataFrozen = true;
        emit MetadataFrozen();
    }

    function setDefaultRoyalty(address receiver, uint96 feeNumerator) external onlyOwner {
        _setDefaultRoyalty(receiver, feeNumerator);
    }

    function deleteDefaultRoyalty() external onlyOwner {
        _deleteDefaultRoyalty();
    }

    function pause() external onlyOwner {
        _pause();
    }

    function unpause() external onlyOwner {
        _unpause();
    }

    // ---------------------------------------------------------------- overrides

    function _update(address to, uint256 tokenId, address auth)
        internal
        override(ERC721Enumerable)
        returns (address)
    {
        return super._update(to, tokenId, auth);
    }

    function _increaseBalance(address account, uint128 value) internal override(ERC721Enumerable) {
        super._increaseBalance(account, value);
    }

    function supportsInterface(bytes4 interfaceId) public view override(ERC721Enumerable, ERC2981) returns (bool) {
        return interfaceId == bytes4(0x49064906) || super.supportsInterface(interfaceId);
    }
}
