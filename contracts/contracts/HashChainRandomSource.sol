// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

import {Ownable} from "@openzeppelin/contracts/access/Ownable.sol";
import {Ownable2Step} from "@openzeppelin/contracts/access/Ownable2Step.sol";

interface IRandomConsumer {
    function fulfillRandomness(uint256 tokenId, uint256 word) external;
}

/// @title Hash-chain random source for CMBGPassCard (stand-in until a VRF exists on Robinhood Chain)
/// @notice The operator keeps a secret chain h0 = seed, h(i) = keccak256(h(i-1)) and publishes the end (anchor = hN).
///         Requests are served strictly in order; each reveal must be the preimage of the current anchor, so the operator
///         cannot choose results. Known weakness: the operator can foresee results and delay them. See SPEC.md.
contract HashChainRandomSource is Ownable2Step {
    address public immutable consumer;
    address public operator;
    bytes32 public anchor;

    uint256 public head; // next queue index to serve
    uint256 public tail; // next free queue index
    mapping(uint256 => uint256) public queue; // index -> tokenId

    event Requested(uint256 indexed index, uint256 indexed tokenId);
    event Revealed(uint256 indexed index, uint256 indexed tokenId, bytes32 preimage, uint256 word);
    event AnchorSet(bytes32 anchor);
    event OperatorSet(address operator);

    error NotConsumer();
    error NotOperator();
    error NothingPending();
    error BadPreimage();
    error QueueNotEmpty();

    constructor(address initialOwner, address consumer_, address operator_, bytes32 anchor_) Ownable(initialOwner) {
        consumer = consumer_;
        operator = operator_;
        anchor = anchor_;
        emit OperatorSet(operator_);
        emit AnchorSet(anchor_);
    }

    function pending() external view returns (uint256) {
        return tail - head;
    }

    function request(uint256 tokenId) external {
        if (msg.sender != consumer) revert NotConsumer();
        uint256 index = tail++;
        queue[index] = tokenId;
        emit Requested(index, tokenId);
    }

    function reveal(bytes32 preimage) public {
        if (msg.sender != operator) revert NotOperator();
        if (head == tail) revert NothingPending();
        if (keccak256(abi.encodePacked(preimage)) != anchor) revert BadPreimage();
        anchor = preimage;
        uint256 index = head++;
        uint256 tokenId = queue[index];
        delete queue[index];
        uint256 word = uint256(keccak256(abi.encode(preimage, tokenId, index)));
        emit Revealed(index, tokenId, preimage, word);
        IRandomConsumer(consumer).fulfillRandomness(tokenId, word);
    }

    function revealBatch(bytes32[] calldata preimages) external {
        for (uint256 i; i < preimages.length; ++i) reveal(preimages[i]);
    }

    /// @notice New chain once the old one runs low. Only when nothing is waiting, so pending results cannot be steered.
    function setAnchor(bytes32 anchor_) external onlyOwner {
        if (head != tail) revert QueueNotEmpty();
        anchor = anchor_;
        emit AnchorSet(anchor_);
    }

    function setOperator(address operator_) external onlyOwner {
        operator = operator_;
        emit OperatorSet(operator_);
    }
}
