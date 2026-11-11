// Mainnet admin calls go through the multisig (Safe): this prints the "To" address and the hex "Data" to paste into the
// Safe app (New transaction -> Transaction builder -> custom data, value 0). Read-only: no key, sends nothing.
//
// usage: node scripts/safe_calldata.js <network> <action> [arg]
//   network: robinhoodMainnet | robinhoodTestnet     (addresses from deployments/<network>.json)
//   actions:
//     acceptOwnership                 both contracts (run once after deploy.js offered the ownership)
//     setBaseURI ipfs://<CID>/        card
//     setMerkleRoot 0x<root>          card (new snapshot)
//     setClaimDeadline <unix seconds> card (0 = open-ended)
//     setOperator 0x<wallet>          random source (reveal Worker wallet)
//     setRandomSource 0x<source>      card
//     pause | unpause                 card
//     freezeMetadata                  card - FINAL, cannot be undone
//     setDefaultRoyalty 0x<receiver> <bps>   card (500 = 5 %)
const fs = require("fs");
const path = require("path");
const { Interface, getAddress } = require("ethers");

const card = new Interface([
  "function acceptOwnership()",
  "function setBaseURI(string)",
  "function setMerkleRoot(bytes32)",
  "function setClaimDeadline(uint64)",
  "function setRandomSource(address)",
  "function pause()",
  "function unpause()",
  "function freezeMetadata()",
  "function setDefaultRoyalty(address,uint96)",
]);
const source = new Interface(["function acceptOwnership()", "function setOperator(address)"]);

const [net, action, a1, a2] = process.argv.slice(2);
if (!net || !action) {
  console.log(fs.readFileSync(__filename, "utf8").split("\n").filter((l) => l.startsWith("//")).join("\n"));
  process.exit(1);
}
const dep = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "deployments", `${net}.json`), "utf8"));
const out = (label, to, data) => console.log(`\n${label}\n  To:    ${to}\n  Value: 0\n  Data:  ${data}`);

switch (action) {
  case "acceptOwnership":
    out("1) CMBGPassCard.acceptOwnership()", dep.CMBGPassCard, card.encodeFunctionData("acceptOwnership"));
    out("2) HashChainRandomSource.acceptOwnership()", dep.HashChainRandomSource, source.encodeFunctionData("acceptOwnership"));
    break;
  case "setBaseURI":
    if (!/^ipfs:\/\/.+\/$/.test(a1 || "")) throw new Error("need ipfs://<CID>/ with the trailing slash");
    out(`CMBGPassCard.setBaseURI("${a1}")`, dep.CMBGPassCard, card.encodeFunctionData("setBaseURI", [a1]));
    break;
  case "setMerkleRoot":
    out(`CMBGPassCard.setMerkleRoot(${a1})`, dep.CMBGPassCard, card.encodeFunctionData("setMerkleRoot", [a1]));
    break;
  case "setClaimDeadline":
    out(`CMBGPassCard.setClaimDeadline(${a1}) = ${new Date(Number(a1) * 1000).toISOString()}`, dep.CMBGPassCard,
      card.encodeFunctionData("setClaimDeadline", [BigInt(a1)]));
    break;
  case "setOperator":
    out(`HashChainRandomSource.setOperator(${getAddress(a1)})`, dep.HashChainRandomSource, source.encodeFunctionData("setOperator", [getAddress(a1)]));
    break;
  case "setRandomSource":
    out(`CMBGPassCard.setRandomSource(${getAddress(a1)})`, dep.CMBGPassCard, card.encodeFunctionData("setRandomSource", [getAddress(a1)]));
    break;
  case "pause":
  case "unpause":
  case "freezeMetadata":
    out(`CMBGPassCard.${action}()${action === "freezeMetadata" ? "  <-- FINAL, cannot be undone" : ""}`, dep.CMBGPassCard, card.encodeFunctionData(action));
    break;
  case "setDefaultRoyalty":
    out(`CMBGPassCard.setDefaultRoyalty(${getAddress(a1)}, ${a2} bps)`, dep.CMBGPassCard,
      card.encodeFunctionData("setDefaultRoyalty", [getAddress(a1), BigInt(a2)]));
    break;
  default:
    throw new Error("unknown action " + action);
}
console.log(`\nnetwork ${net} (chain ${dep.chainId}) - check the To address on the explorer before signing.`);
