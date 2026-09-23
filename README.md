# pins-guider

Native autoguider for [PINS](https://github.com/aduffeck/pins) with a Touch-N-Stars UI —
PHD2-level precision, INDI guide cameras, multi-star guiding. See [docs/DESIGN.md](docs/DESIGN.md).

## Layout

| Path | Content |
|---|---|
| `src/PinsGuider.Engine` | Host-agnostic guiding engine (pure C#, no NINA dependency) |
| `tests/PinsGuider.Engine.Tests` | NUnit tests, synthetic star fields, closed-loop simulator tests |
| `tools/phd2-parity` | Harness comparing star measurement against PHD2's own `star.cpp` |

## Build & test

No local .NET SDK needed — `./build.sh` runs inside `mcr.microsoft.com/dotnet/sdk:10.0`:

```sh
./build.sh                      # dotnet test PinsGuider.slnx
./build.sh build PinsGuider.slnx
```

## License

MPL-2.0. Portions ported from PHD2 are additionally BSD-3-Clause; see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
