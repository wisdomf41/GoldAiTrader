#property copyright "GoldAiTrader"
#property version   "1.00"
#property strict
#property description "Demo-first authenticated MT5 telemetry bridge. No order execution."

#include "Include\GoldAiTraderBridgeConfig.mqh"
#include "Include\GoldAiTraderBridgeCrypto.mqh"
#include "Include\GoldAiTraderBridgeJson.mqh"
#include "Include\GoldAiTraderBridgeHttp.mqh"
#include "Include\GoldAiTraderBridgeState.mqh"

input string InpBridgeBaseUrl="http://127.0.0.1:5088";
input string InpBridgeInstanceId="";
input string InpCanonicalSymbol="XAUUSD";
input string InpBrokerSymbol="";
input string InpSecretFile="GoldAiTrader\\bridge.secret";
input int    InpHeartbeatSeconds=5;
input int    InpSnapshotSeconds=10;
input int    InpHttpTimeoutMilliseconds=2000;
input int    InpMaximumBodyBytes=262144;
input int    InpMaximumRetryBackoffSeconds=30;
input bool   InpPublishM5=true;
input bool   InpPublishM15=true;

CGatUtcClock      g_clock;
CGatHttpClient    g_http;
CGatTerminalState g_state;

bool     g_initialized=false;
bool     g_connection_published=false;
bool     g_last_connected=false;
bool     g_session_ready=false;
datetime g_last_heartbeat_local=0;
datetime g_last_snapshot_local=0;
datetime g_last_m5_open=0;
datetime g_last_m15_open=0;
datetime g_next_attempt_local=0;
int      g_consecutive_failures=0;

void ScheduleRetry()
  {
   g_consecutive_failures++;
   int exponent=MathMin(g_consecutive_failures-1,5);
   int delay=1<<exponent;
   delay=MathMin(delay,InpMaximumRetryBackoffSeconds);
   g_next_attempt_local=TimeLocal()+delay;
  }

void RecordSuccess()
  {
   g_consecutive_failures=0;
   g_next_attempt_local=0;
  }

bool PublishConnection(const bool connected,const string detail)
  {
   string timestamp=g_clock.Now();
   if(StringLen(timestamp)==0)
      return false;
   return g_http.Post(GAT_ROUTE_CONNECTION,timestamp,
                      g_state.ConnectionBody(timestamp,connected,detail));
  }

bool PublishHeartbeat(const bool connected)
  {
   string timestamp=g_clock.Now();
   if(StringLen(timestamp)==0)
      return false;
   return g_http.Post(GAT_ROUTE_HEARTBEAT,timestamp,
                      g_state.HeartbeatBody(timestamp,connected));
  }

bool PublishSnapshot()
  {
   string symbol_timestamp=g_clock.Now();
   string symbol_body="";
   double tick_size=0.0,tick_value=0.0;
   if(StringLen(symbol_timestamp)==0 ||
      !g_state.SymbolBody(symbol_timestamp,symbol_body,tick_size,tick_value))
     {
      Print("GoldAiTrader bridge withheld an invalid symbol snapshot.");
      return false;
     }
   if(!g_http.Post(GAT_ROUTE_SYMBOL,symbol_timestamp,symbol_body))
      return false;

   string account_timestamp=g_clock.Now();
   string positions_timestamp=g_clock.Now();
   string account_body="",positions_body="";
   if(!g_state.AccountAndPositions(account_timestamp,positions_timestamp,
                                   tick_size,tick_value,account_body,positions_body))
     {
      Print("GoldAiTrader bridge withheld account and position state because it was incomplete.");
      return false;
     }
   if(!g_http.Post(GAT_ROUTE_ACCOUNT,account_timestamp,account_body))
      return false;
   if(!g_http.Post(GAT_ROUTE_POSITIONS,positions_timestamp,positions_body))
      return false;
   return true;
  }

bool PublishCompletedBar(const ENUM_TIMEFRAMES timeframe,datetime &last_open)
  {
   string timestamp=g_clock.Now();
   string body="";
   datetime open_time=0;
   int result=g_state.CompletedBarBody(timeframe,last_open,timestamp,g_clock,body,open_time);
   if(result==0)
      return true;
   if(result<0 || !g_http.Post(GAT_ROUTE_BAR,timestamp,body))
      return false;
   last_open=open_time;
   return true;
  }

bool PublishBars()
  {
   if(InpPublishM5 && !PublishCompletedBar(PERIOD_M5,g_last_m5_open))
      return false;
   if(InpPublishM15 && !PublishCompletedBar(PERIOD_M15,g_last_m15_open))
      return false;
   return true;
  }

int OnInit()
  {
   if((bool)MQLInfoInteger(MQL_TESTER))
     {
      Print("GoldAiTrader bridge cannot use WebRequest in the Strategy Tester.");
      return INIT_FAILED;
     }
   if(!GatIsValidLoopbackBaseUrl(InpBridgeBaseUrl) ||
      StringLen(InpBridgeInstanceId)==0 || StringLen(InpCanonicalSymbol)==0 ||
      StringLen(InpBrokerSymbol)==0 || InpHeartbeatSeconds<1 || InpHeartbeatSeconds>10 ||
      InpSnapshotSeconds<1 || InpSnapshotSeconds>10 ||
      InpHttpTimeoutMilliseconds<100 || InpHttpTimeoutMilliseconds>5000 ||
      InpMaximumBodyBytes<1024 || InpMaximumBodyBytes>1048576 ||
      InpMaximumRetryBackoffSeconds<1 || InpMaximumRetryBackoffSeconds>60)
     {
      Print("GoldAiTrader bridge configuration is invalid; startup failed closed.");
      return INIT_PARAMETERS_INCORRECT;
     }

   g_clock.Initialize();
   if(!g_state.Initialize(InpBridgeInstanceId,InpCanonicalSymbol,InpBrokerSymbol))
     {
      Print("GoldAiTrader bridge terminal identity or symbol mapping is unavailable.");
      return INIT_FAILED;
     }

   string secret="";
   if(!GatLoadSecret(InpSecretFile,secret))
      return INIT_FAILED;
   if(!g_http.Initialize(InpBridgeBaseUrl,InpBridgeInstanceId,g_state.AccountId(),secret,
                         InpHttpTimeoutMilliseconds,InpMaximumBodyBytes))
     {
      secret="";
      Print("GoldAiTrader bridge HTTP client initialization failed closed.");
      return INIT_FAILED;
     }
   secret="";

   if(!EventSetTimer(1))
     {
      g_http.ClearSecret();
      Print("GoldAiTrader bridge timer initialization failed.");
      return INIT_FAILED;
     }

   g_initialized=true;
   Print("GoldAiTrader MT5 telemetry bridge initialized; execution is not implemented.");
   return INIT_SUCCEEDED;
  }

void OnDeinit(const int reason)
  {
   EventKillTimer();
   if(g_initialized && g_connection_published)
      PublishConnection(false,"ea_stopped");
   g_http.ClearSecret();
   g_initialized=false;
  }

void OnTimer()
  {
   if(!g_initialized || (g_next_attempt_local>0 && TimeLocal()<g_next_attempt_local))
      return;

   bool connected=(bool)TerminalInfoInteger(TERMINAL_CONNECTED);
   if(!g_connection_published || connected!=g_last_connected)
     {
      if(!PublishConnection(connected,connected ? "connected" : "disconnected"))
        {
         ScheduleRetry();
         return;
        }
      g_connection_published=true;
      g_last_connected=connected;
      g_session_ready=false;
      g_last_m5_open=0;
      g_last_m15_open=0;
      RecordSuccess();
     }

   if(!connected)
      return;

   datetime now=TimeLocal();
   if(!g_session_ready)
     {
      if(!PublishHeartbeat(true) || !PublishSnapshot())
        {
         ScheduleRetry();
         return;
        }
      g_session_ready=true;
      g_last_heartbeat_local=now;
      g_last_snapshot_local=now;
      RecordSuccess();
     }
   else
     {
      if(now-g_last_heartbeat_local>=InpHeartbeatSeconds)
        {
         if(!PublishHeartbeat(true))
           {
            ScheduleRetry();
            return;
           }
         g_last_heartbeat_local=now;
         RecordSuccess();
        }
      if(now-g_last_snapshot_local>=InpSnapshotSeconds)
        {
         if(!PublishSnapshot())
           {
            ScheduleRetry();
            return;
           }
         g_last_snapshot_local=now;
         RecordSuccess();
        }
     }

   if(!PublishBars())
      ScheduleRetry();
  }
