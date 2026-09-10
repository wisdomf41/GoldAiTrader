# Broker and Platform Independence

GoldAiTrader is broker-agnostic and platform-agnostic. cTrader is the first planned platform
adapter, not part of the core trading architecture. IC Markets is only an initial demo broker
used during development and testing; it is not a permanent requirement.

Core, Market, Risk, Strategy, Backtesting, Data, and ML consume canonical symbols and
GoldAiTrader-owned normalized models. An adapter maps broker names such as `GOLD`, `XAUUSD.a`,
or `XAUUSDm` to canonical `XAUUSD`, obtains account and symbol specifications dynamically, and
translates neutral execution requests/results at the external boundary.

## Adding a future adapter

Create a dedicated adapter project that references Core and Execution, then implement the
applicable normalized boundaries:

- `IMarketDataProvider`
- `ITradingAccountProvider`
- `ISymbolSpecificationProvider`
- `IPositionProvider`
- `IExecutionGateway`

SDK types must stay inside that adapter. Translate platform market data, accounts, positions,
symbol metadata, and order results into GoldAiTrader models. Strategy and Risk must continue to
use canonical identities and dynamic `SymbolSpecification` values.

A future MetaTrader 5, REST, or FIX adapter can follow this process without changes to trading,
risk, research, persistence, or ML projects. No fake MT5 implementation is included.
