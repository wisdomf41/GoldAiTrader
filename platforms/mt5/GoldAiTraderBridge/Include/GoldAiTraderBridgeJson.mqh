#ifndef GOLDAITRADER_BRIDGE_JSON_MQH
#define GOLDAITRADER_BRIDGE_JSON_MQH

string GatJsonEscape(const string value)
  {
   string escaped="";
   for(int index=0;index<StringLen(value);index++)
     {
      ushort character=StringGetCharacter(value,index);
      if(character=='\"')
         escaped+="\\\"";
      else if(character=='\\')
         escaped+="\\\\";
      else if(character=='\8')
         escaped+="\\b";
      else if(character=='\12')
         escaped+="\\f";
      else if(character=='\n')
         escaped+="\\n";
      else if(character=='\r')
         escaped+="\\r";
      else if(character=='\t')
         escaped+="\\t";
      else if(character<32)
         escaped+=StringFormat("\\u%04x",(uint)character);
      else
         escaped+=ShortToString(character);
     }
   return escaped;
  }

string GatJsonString(const string value)
  {
   return "\""+GatJsonEscape(value)+"\"";
  }

string GatJsonNumber(const double value)
  {
   if(!MathIsValidNumber(value))
      return "";
   string text=DoubleToString(value,12);
   while(StringFind(text,".")>=0 && StringLen(text)>0 &&
         StringSubstr(text,StringLen(text)-1)=="0")
      text=StringSubstr(text,0,StringLen(text)-1);
   if(StringLen(text)>0 && StringSubstr(text,StringLen(text)-1)==".")
      text=StringSubstr(text,0,StringLen(text)-1);
   if(text=="-0")
      text="0";
   return text;
  }

string GatJsonBool(const bool value)
  {
   return value ? "true" : "false";
  }

string GatEnvelopeJson(const string protocol_version,const string bridge_id,
                       const string sent_at_utc)
  {
   return "{\"protocolVersion\":"+GatJsonString(protocol_version)+
          ",\"bridgeInstanceId\":"+GatJsonString(bridge_id)+
          ",\"sentAtUtc\":"+GatJsonString(sent_at_utc)+"}";
  }

string GatTimeframeJson(const ENUM_TIMEFRAMES timeframe)
  {
   int total=PeriodSeconds(timeframe);
   if(total<=0)
      return "";
   int days=total/86400;
   int remaining=total%86400;
   int hours=remaining/3600;
   int minutes=(remaining%3600)/60;
   int seconds=remaining%60;
   if(days>0)
      return StringFormat("%d.%02d:%02d:%02d",days,hours,minutes,seconds);
   return StringFormat("%02d:%02d:%02d",hours,minutes,seconds);
  }

#endif
