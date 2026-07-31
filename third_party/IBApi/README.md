# Vendored IBApi (Interactive Brokers TWS API, C# client)

**Do not edit these sources.** They are third-party code, replaced wholesale on upgrade.

## Provenance

| | |
|---|---|
| Version | **TWS API 10.45.01** (the *Stable* channel, not *Latest* 10.48.01) |
| Source | `TWS API Install 1045.01.msi` from <https://interactivebrokers.github.io/> |
| Vendored | 2026-07-31 |
| Contents | 68 core `IBApi` types + 203 generated `IBApi.protobuf` types |

IBKR does not publish an official NuGet package. The community packages on nuget.org
(`OpenTws.IbApi` 9.79, `Integrative.IbApi`, `TWS`, `InterReact`) are unofficial forks pinned to
9.x — several major versions behind, with callback signatures that no longer match a current
TWS/Gateway. Hence vendoring the official source.

The C# client ships **only in the Windows installer**; the `twsapi_macunix` zip contains just the
Java and C++ APIs. On Linux the `.msi` is extracted with `7z`.

## Filename caveat

The MSI stores payload files under mangled keys (`filA5FF…`), so the original filenames are not
recoverable from the archive. Filenames here were **reconstructed from the primary public type in
each file**. For files declaring several public types the name reflects only the first one — most
notably:

- `src/Liquidity.cs` also declares **`Execution`**, `OptionExerciseType`, `COptionExerciseType`
- `src/TriggerMethod.cs` also declares `CTriggerMethod`, `PriceCondition`
- `src/OrderConditionType.cs` also declares `OrderCondition`
- `src/ContractDetails.cs` also declares the fund-distribution/asset-type enums
- `src/EClientErrors.cs` also declares `CodeMsgPair`

This is cosmetic — C# does not require a filename to match its type. All 271 types are present and
namespaces are unchanged. Use symbol search, not filenames, to navigate.

## Notable API changes in 10.45 vs older docs

- `CommissionReport` is now **`CommissionAndFeesReport`**; the `EWrapper` callback is
  `commissionAndFeesReport`. Most tutorials online still show the old name.
- `EWrapper.error` signature is
  `error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)` —
  the `errorTime` parameter does not exist in pre-10.30 examples.
- Message bodies are increasingly protobuf-backed, hence the `Google.Protobuf` dependency.

Check `src/EWrapper.cs` for the authoritative callback list before writing against any tutorial.

## Upgrading

1. Download the new `TWS API Install <ver>.msi`.
2. `7z x -oextracted "TWS API Install <ver>.msi"`
3. Keep every extracted file containing `namespace IBApi`, naming each by its primary public type;
   put `namespace IBApi.protobuf` files under `src/protobuf/`.
4. Rebuild and fix any `IbkrGateway` compile breaks — callback signatures do change between versions.
5. Update the version in this file and in `docs/STATE.md`.

## License

Copyright © Interactive Brokers LLC. Subject to the **IB API Non-Commercial License** or the **IB
API Commercial License**, as applicable.

The non-commercial license covers personal/internal use. **If this application is ever operated
commercially, a separate commercial license from IBKR is required.** That is a licensing decision
for the project owner, not something the code can assume.
