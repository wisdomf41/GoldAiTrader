#ifndef GOLDAITRADER_BRIDGE_STATE_MQH
#define GOLDAITRADER_BRIDGE_STATE_MQH

class CGatTerminalState
  {
private:
   string m_bridge_id;
   string m_canonical_symbol;
   string m_broker_symbol;
   string m_account_id;
   string m_global_prefix;

   double GlobalValue(const string suffix,const double fallback)
     {
      string name=m_global_prefix+suffix;
      if(!GlobalVariableCheck(name))
        {
         GlobalVariableSet(name,fallback);
         return fallback;
        }
      return GlobalVariableGet(name);
     }

   void SetGlobalValue(const string suffix,const double value)
     {
      GlobalVariableSet(m_global_prefix+suffix,value);
     }

   bool ReadSymbolValues(double &tick_size,double &tick_value,double &contract_size,
                         double &minimum_volume,double &maximum_volume,double &volume_step,
                         double &minimum_stop_distance,bool &trading_available)
     {
      double point=0.0;
      if(!SymbolInfoDouble(m_broker_symbol,SYMBOL_TRADE_TICK_SIZE,tick_size) ||
         !SymbolInfoDouble(m_broker_symbol,SYMBOL_TRADE_TICK_VALUE_LOSS,tick_value) ||
         !SymbolInfoDouble(m_broker_symbol,SYMBOL_TRADE_CONTRACT_SIZE,contract_size) ||
         !SymbolInfoDouble(m_broker_symbol,SYMBOL_VOLUME_MIN,minimum_volume) ||
         !SymbolInfoDouble(m_broker_symbol,SYMBOL_VOLUME_MAX,maximum_volume) ||
         !SymbolInfoDouble(m_broker_symbol,SYMBOL_VOLUME_STEP,volume_step) ||
         !SymbolInfoDouble(m_broker_symbol,SYMBOL_POINT,point))
         return false;
      long stop_points=SymbolInfoInteger(m_broker_symbol,SYMBOL_TRADE_STOPS_LEVEL);
      long trade_mode=SymbolInfoInteger(m_broker_symbol,SYMBOL_TRADE_MODE);
      long order_mode=SymbolInfoInteger(m_broker_symbol,SYMBOL_ORDER_MODE);
      minimum_stop_distance=(double)stop_points*point;
      trading_available=(bool)TerminalInfoInteger(TERMINAL_CONNECTED) &&
                        trade_mode==SYMBOL_TRADE_MODE_FULL &&
                        (order_mode&SYMBOL_ORDER_MARKET)==SYMBOL_ORDER_MARKET;
      return tick_size>0.0 && tick_value>0.0 && contract_size>0.0 &&
             minimum_volume>0.0 && maximum_volume>=minimum_volume &&
             volume_step>0.0 && minimum_stop_distance>=0.0;
     }

   bool RiskReferences(const double balance,const double equity,double &day_start,
                       double &week_start,double &peak_balance,double &peak_equity)
     {
      if(balance<=0.0 || equity<=0.0)
         return false;
      datetime now=TimeGMT();
      MqlDateTime parts;
      if(!TimeToStruct(now,parts))
         return false;
      double day_key=(double)(parts.year*10000+parts.mon*100+parts.day);
      int days_since_monday=(parts.day_of_week+6)%7;
      MqlDateTime week_parts;
      if(!TimeToStruct((datetime)((long)now-(long)days_since_monday*86400),week_parts))
         return false;
      double week_key=(double)(week_parts.year*10000+week_parts.mon*100+week_parts.day);

      if(GlobalValue("DayKey",day_key)!=day_key)
        {
         SetGlobalValue("DayKey",day_key);
         SetGlobalValue("DayStartEquity",equity);
        }
      if(GlobalValue("WeekKey",week_key)!=week_key)
        {
         SetGlobalValue("WeekKey",week_key);
         SetGlobalValue("WeekStartEquity",equity);
        }

      day_start=GlobalValue("DayStartEquity",equity);
      week_start=GlobalValue("WeekStartEquity",equity);
      peak_balance=MathMax(GlobalValue("PeakBalance",balance),balance);
      peak_equity=MathMax(GlobalValue("PeakEquity",equity),equity);
      SetGlobalValue("PeakBalance",peak_balance);
      SetGlobalValue("PeakEquity",peak_equity);
      return day_start>0.0 && week_start>0.0 && peak_balance>0.0 && peak_equity>0.0;
     }

   string EnvironmentName() const
     {
      ENUM_ACCOUNT_TRADE_MODE mode=(ENUM_ACCOUNT_TRADE_MODE)AccountInfoInteger(ACCOUNT_TRADE_MODE);
      if(mode==ACCOUNT_TRADE_MODE_DEMO)
         return "demo";
      if(mode==ACCOUNT_TRADE_MODE_REAL)
         return "live";
      return "unknown";
     }

public:
   bool Initialize(const string bridge_id,const string canonical_symbol,
                   const string broker_symbol)
     {
      long login=AccountInfoInteger(ACCOUNT_LOGIN);
      if(StringLen(bridge_id)==0 || StringLen(canonical_symbol)==0 ||
         StringLen(broker_symbol)==0 || login<=0)
         return false;
      if(!SymbolSelect(broker_symbol,true))
         return false;
      m_bridge_id=bridge_id;
      m_canonical_symbol=canonical_symbol;
      m_broker_symbol=broker_symbol;
      m_account_id=StringFormat("%I64d",login);
      m_global_prefix="GAT."+m_account_id+".";
      return true;
     }

   string AccountId() const
     {
      return m_account_id;
     }

   string HeartbeatBody(const string timestamp,const bool connected) const
     {
      return "{\"envelope\":"+GatEnvelopeJson(GAT_PROTOCOL_VERSION,m_bridge_id,timestamp)+
             ",\"terminalConnected\":"+GatJsonBool(connected)+"}";
     }

   string ConnectionBody(const string timestamp,const bool connected,const string detail) const
     {
      return "{\"envelope\":"+GatEnvelopeJson(GAT_PROTOCOL_VERSION,m_bridge_id,timestamp)+
             ",\"terminalConnected\":"+GatJsonBool(connected)+
             ",\"detail\":"+GatJsonString(detail)+"}";
     }

   bool SymbolBody(const string timestamp,string &body,double &tick_size,double &tick_value)
     {
      double contract_size,minimum_volume,maximum_volume,volume_step,minimum_stop_distance;
      bool trading_available;
      if(!ReadSymbolValues(tick_size,tick_value,contract_size,minimum_volume,
                           maximum_volume,volume_step,minimum_stop_distance,trading_available))
         return false;
      string description="{\"brokerSymbol\":"+GatJsonString(m_broker_symbol)+
                         ",\"canonicalSymbol\":"+GatJsonString(m_canonical_symbol)+
                         ",\"tickSize\":"+GatJsonNumber(tick_size)+
                         ",\"tickValuePerVolumeUnit\":"+GatJsonNumber(tick_value)+
                         ",\"contractSize\":"+GatJsonNumber(contract_size)+
                         ",\"minVolume\":"+GatJsonNumber(minimum_volume)+
                         ",\"maxVolume\":"+GatJsonNumber(maximum_volume)+
                         ",\"volumeStep\":"+GatJsonNumber(volume_step)+
                         ",\"minStopDistance\":"+GatJsonNumber(minimum_stop_distance)+
                         ",\"isTradingAvailable\":"+GatJsonBool(trading_available)+"}";
      body="{\"envelope\":"+GatEnvelopeJson(GAT_PROTOCOL_VERSION,m_bridge_id,timestamp)+
           ",\"symbol\":"+description+"}";
      return true;
     }

   bool AccountAndPositions(const string account_timestamp,const string positions_timestamp,
                            const double tick_size,const double tick_value,
                            string &account_body,string &positions_body)
     {
      if(tick_size<=0.0 || tick_value<=0.0)
         return false;
      int total=PositionsTotal();
      double open_risk=0.0;
      string positions="[";
      for(int index=0;index<total;index++)
        {
         ulong ticket=PositionGetTicket(index);
         if(ticket==0)
            return false;
         string symbol=PositionGetString(POSITION_SYMBOL);
         if(symbol!=m_broker_symbol)
           {
            Print("GoldAiTrader position inventory withheld: an unmapped broker symbol is open.");
            return false;
           }
         long identifier=PositionGetInteger(POSITION_IDENTIFIER);
         ENUM_POSITION_TYPE type=(ENUM_POSITION_TYPE)PositionGetInteger(POSITION_TYPE);
         double volume=PositionGetDouble(POSITION_VOLUME);
         double entry=PositionGetDouble(POSITION_PRICE_OPEN);
         double stop_loss=PositionGetDouble(POSITION_SL);
         double take_profit=PositionGetDouble(POSITION_TP);
         if(identifier<=0 || (type!=POSITION_TYPE_BUY && type!=POSITION_TYPE_SELL) ||
            volume<=0.0 || entry<=0.0 || stop_loss<=0.0)
           {
            Print("GoldAiTrader position inventory withheld: required position risk data is incomplete.");
            return false;
           }
         double risk=MathAbs(entry-stop_loss)/tick_size*tick_value*volume;
         if(!MathIsValidNumber(risk) || risk<0.0)
            return false;
         open_risk+=risk;
         if(index>0)
            positions+=",";
         positions+="{\"positionId\":"+GatJsonString(StringFormat("%I64d",identifier))+
                    ",\"brokerSymbol\":"+GatJsonString(m_broker_symbol)+
                    ",\"canonicalSymbol\":"+GatJsonString(m_canonical_symbol)+
                    ",\"direction\":"+GatJsonString(type==POSITION_TYPE_BUY ? "buy" : "sell")+
                    ",\"volume\":"+GatJsonNumber(volume)+
                    ",\"entryPrice\":"+GatJsonNumber(entry)+
                    ",\"stopLoss\":"+GatJsonNumber(stop_loss)+
                    ",\"takeProfit\":"+(take_profit>0.0 ? GatJsonNumber(take_profit) : "null")+
                    ",\"currentRiskAmount\":"+GatJsonNumber(risk)+
                    ",\"signalId\":null,\"clientCorrelationId\":null,"+
                    "\"strategyVersion\":null,\"ownershipTag\":null}";
        }
      positions+="]";

      double balance=AccountInfoDouble(ACCOUNT_BALANCE);
      double equity=AccountInfoDouble(ACCOUNT_EQUITY);
      double free_margin=AccountInfoDouble(ACCOUNT_MARGIN_FREE);
      double day_start,week_start,peak_balance,peak_equity;
      string currency=AccountInfoString(ACCOUNT_CURRENCY);
      if(StringLen(currency)==0 || free_margin<0.0 ||
         !RiskReferences(balance,equity,day_start,week_start,peak_balance,peak_equity))
         return false;

      string account="{\"accountIdentifier\":"+GatJsonString(m_account_id)+
                     ",\"accountCurrency\":"+GatJsonString(currency)+
                     ",\"balance\":"+GatJsonNumber(balance)+
                     ",\"equity\":"+GatJsonNumber(equity)+
                     ",\"dayStartEquity\":"+GatJsonNumber(day_start)+
                     ",\"weekStartEquity\":"+GatJsonNumber(week_start)+
                     ",\"peakBalance\":"+GatJsonNumber(peak_balance)+
                     ",\"peakEquity\":"+GatJsonNumber(peak_equity)+
                     ",\"openPositions\":"+IntegerToString(total)+
                     ",\"openRiskAmount\":"+GatJsonNumber(open_risk)+
                     ",\"freeMargin\":"+GatJsonNumber(free_margin)+
                     ",\"environment\":"+GatJsonString(EnvironmentName())+
                     ",\"emergencyShutdown\":false}";
      account_body="{\"envelope\":"+
                   GatEnvelopeJson(GAT_PROTOCOL_VERSION,m_bridge_id,account_timestamp)+
                   ",\"account\":"+account+"}";
      positions_body="{\"envelope\":"+
                     GatEnvelopeJson(GAT_PROTOCOL_VERSION,m_bridge_id,positions_timestamp)+
                     ",\"accountIdentifier\":"+GatJsonString(m_account_id)+
                     ",\"positions\":"+positions+"}";
      return true;
     }

   int CompletedBarBody(const ENUM_TIMEFRAMES timeframe,const datetime last_open_time,
                        const string timestamp,const CGatUtcClock &clock,string &body,
                        datetime &open_time)
     {
      MqlRates rates[1];
      ResetLastError();
      if(CopyRates(m_broker_symbol,timeframe,1,1,rates)!=1)
   return -1;

if(rates[0].time<=last_open_time)
   return 0;

// Updated: require both broker time and UTC time to be safely past candle close.
int timeframe_seconds=PeriodSeconds(timeframe);
if(timeframe_seconds<=0)
   return -1;

datetime close_time=rates[0].time+timeframe_seconds;
if(TimeCurrent()<close_time)
   return 0;

long raw_offset=(long)TimeTradeServer()-(long)TimeGMT();
long offset=(long)MathRound((double)raw_offset/60.0)*60;
datetime close_time_utc=(datetime)((long)close_time-offset);

if(TimeGMT()<close_time_utc+2)
   return 0;

double point=0.0;
      if(!SymbolInfoDouble(m_broker_symbol,SYMBOL_POINT,point) || point<=0.0)
         return -1;
      string timeframe_text=GatTimeframeJson(timeframe);
      string open_utc=clock.ServerTimeToUtc(rates[0].time);
      if(StringLen(timeframe_text)==0 || StringLen(open_utc)==0)
         return -1;
      double bid=rates[0].close;
      double ask=bid+(double)rates[0].spread*point;
      double volume=rates[0].real_volume>0 ? (double)rates[0].real_volume :
                                             (double)rates[0].tick_volume;
      string bar="{\"brokerSymbol\":"+GatJsonString(m_broker_symbol)+
                 ",\"canonicalSymbol\":"+GatJsonString(m_canonical_symbol)+
                 ",\"timeframe\":"+GatJsonString(timeframe_text)+
                 ",\"openTimeUtc\":"+GatJsonString(open_utc)+
                 ",\"open\":"+GatJsonNumber(rates[0].open)+
                 ",\"high\":"+GatJsonNumber(rates[0].high)+
                 ",\"low\":"+GatJsonNumber(rates[0].low)+
                 ",\"close\":"+GatJsonNumber(rates[0].close)+
                 ",\"bid\":"+GatJsonNumber(bid)+
                 ",\"ask\":"+GatJsonNumber(ask)+
                 ",\"volume\":"+GatJsonNumber(volume)+"}";
      body="{\"envelope\":"+GatEnvelopeJson(GAT_PROTOCOL_VERSION,m_bridge_id,timestamp)+
           ",\"bar\":"+bar+"}";
      open_time=rates[0].time;
      return 1;
     }
  };

#endif
