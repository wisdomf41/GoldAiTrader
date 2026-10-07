#ifndef GOLDAITRADER_BRIDGE_CONFIG_MQH
#define GOLDAITRADER_BRIDGE_CONFIG_MQH

#define GAT_PROTOCOL_VERSION "1.0"
#define GAT_ADAPTER_VERSION  "1.0.0"

#define GAT_ROUTE_HEARTBEAT  "/bridge/mt5/v1/heartbeat"
#define GAT_ROUTE_CONNECTION "/bridge/mt5/v1/connection"
#define GAT_ROUTE_ACCOUNT    "/bridge/mt5/v1/account"
#define GAT_ROUTE_SYMBOL     "/bridge/mt5/v1/symbol"
#define GAT_ROUTE_POSITIONS  "/bridge/mt5/v1/positions"
#define GAT_ROUTE_BAR        "/bridge/mt5/v1/bars/completed"

bool GatIsValidLoopbackBaseUrl(const string value)
  {
   const string prefix="http://127.0.0.1:";
   if(StringFind(value,prefix)!=0)
      return false;

   string port_text=StringSubstr(value,StringLen(prefix));
   int length=StringLen(port_text);
   if(length<1 || length>5)
      return false;

   for(int index=0;index<length;index++)
     {
      ushort character=StringGetCharacter(port_text,index);
      if(character<'0' || character>'9')
         return false;
     }

   long port=StringToInteger(port_text);
   return port>=1 && port<=65535;
  }

bool GatLoadSecret(const string relative_path,string &secret)
  {
   secret="";
   ResetLastError();
   int handle=FileOpen(relative_path,FILE_READ|FILE_TXT|FILE_ANSI|FILE_COMMON,0,CP_UTF8);
   if(handle==INVALID_HANDLE)
     {
      PrintFormat("GoldAiTrader bridge secret file could not be opened (error %d).",GetLastError());
      return false;
     }

   secret=FileReadString(handle);
   FileClose(handle);
   StringTrimLeft(secret);
   StringTrimRight(secret);

   uchar secret_bytes[];
   int copied=StringToCharArray(secret,secret_bytes,0,WHOLE_ARRAY,CP_UTF8);
   if(copied>0 && ArraySize(secret_bytes)>0 && secret_bytes[ArraySize(secret_bytes)-1]==0)
      ArrayResize(secret_bytes,ArraySize(secret_bytes)-1);
   if(ArraySize(secret_bytes)<32)
     {
      secret="";
      ArrayFree(secret_bytes);
      Print("GoldAiTrader bridge secret must contain at least 32 UTF-8 bytes.");
      return false;
     }

   ArrayFree(secret_bytes);
   return true;
  }

class CGatUtcClock
  {
private:
   datetime m_base_utc;
   ulong    m_base_microseconds;
   datetime m_last_utc_second;
uint     m_sequence_microseconds;

   string Format(const datetime seconds,const uint microseconds) const
     {
      MqlDateTime value;
      if(!TimeToStruct(seconds,value))
         return "";
      return StringFormat("%04d-%02d-%02dT%02d:%02d:%02d.%06u0+00:00",
                          value.year,value.mon,value.day,value.hour,value.min,value.sec,
                          microseconds);
     }

public:
   void Initialize()
  {
   // initialize a wall-clock-based monotonic timestamp sequence.
   m_last_utc_second=0;
   m_sequence_microseconds=0;
  }

string Now()
  {
   datetime now=TimeGMT();

   if(now<m_last_utc_second)
      return "";

   if(now>m_last_utc_second)
     {
      m_last_utc_second=now;
      m_sequence_microseconds=0;
     }
   else
     {
      if(m_sequence_microseconds>=999999)
         return "";

      m_sequence_microseconds++;
     }

   return Format(now,m_sequence_microseconds);
  }
     // Updated: restore broker-server-time to UTC conversion.
   string ServerTimeToUtc(const datetime server_time) const
     {
      long raw_offset=(long)TimeTradeServer()-(long)TimeGMT();
      long offset=(long)MathRound((double)raw_offset/60.0)*60;

      return Format((datetime)((long)server_time-offset),0);
     }
  };

#endif
