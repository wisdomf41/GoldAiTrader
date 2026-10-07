#ifndef GOLDAITRADER_BRIDGE_HTTP_MQH
#define GOLDAITRADER_BRIDGE_HTTP_MQH

class CGatHttpClient
  {
private:
   string m_base_url;
   string m_bridge_id;
   string m_account_id;
   string m_secret;
   int    m_timeout_ms;
   int    m_maximum_body_bytes;
   ulong  m_nonce_counter;

public:
   bool Initialize(const string base_url,const string bridge_id,const string account_id,
                   const string secret,const int timeout_ms,const int maximum_body_bytes)
     {
      if(!GatIsValidLoopbackBaseUrl(base_url) || StringLen(bridge_id)==0 ||
         StringLen(account_id)==0 || StringLen(secret)==0 || timeout_ms<100 ||
         maximum_body_bytes<1024 || maximum_body_bytes>1048576)
         return false;
      m_base_url=base_url;
      m_bridge_id=bridge_id;
      m_account_id=account_id;
      m_secret=secret;
      m_timeout_ms=timeout_ms;
      m_maximum_body_bytes=maximum_body_bytes;
      m_nonce_counter=0;
      return true;
     }

   void ClearSecret()
     {
      m_secret="";
     }

   bool Post(const string path,const string timestamp,const string body)
     {
      if(StringFind(path,"/bridge/mt5/v1/")!=0 || StringFind(path,"?")>=0 ||
         StringFind(path,"#")>=0)
         return false;

      uchar body_bytes[];
      if(!GatUtf8Bytes(body,body_bytes))
         return false;
      int body_size=ArraySize(body_bytes);
      if(body_size<1 || body_size>m_maximum_body_bytes)
        {
         PrintFormat("GoldAiTrader bridge request %s rejected locally for invalid size.",path);
         return false;
        }

      string body_hash="";
      if(!GatSha256Hex(body_bytes,body_hash))
        {
         PrintFormat("GoldAiTrader bridge request %s could not be hashed.",path);
         return false;
        }

      string nonce=GatCreateNonce(m_secret,m_bridge_id,m_account_id,m_nonce_counter);
      if(StringLen(nonce)<16)
        {
         PrintFormat("GoldAiTrader bridge request %s could not create a nonce.",path);
         return false;
        }

      string canonical=m_bridge_id+"\n"+GAT_PROTOCOL_VERSION+"\nPOST\n"+path+"\n"+
                       timestamp+"\n"+nonce+"\n"+body_hash;
      string signature="";
      if(!GatHmacSha256Hex(m_secret,canonical,signature))
        {
         PrintFormat("GoldAiTrader bridge request %s could not be signed.",path);
         return false;
        }

      string headers="Content-Type: application/json\r\n"+
                     "X-GAT-Bridge-Id: "+m_bridge_id+"\r\n"+
                     "X-GAT-Protocol-Version: "+GAT_PROTOCOL_VERSION+"\r\n"+
                     "X-GAT-Timestamp: "+timestamp+"\r\n"+
                     "X-GAT-Nonce: "+nonce+"\r\n"+
                     "X-GAT-Signature: "+signature+"\r\n";

      char request_data[];
      ArrayResize(request_data,body_size);
      for(int index=0;index<body_size;index++)
         request_data[index]=(char)body_bytes[index];
      char response_data[];
      string response_headers="";
      ResetLastError();
      int status=WebRequest("POST",m_base_url+path,headers,m_timeout_ms,
                            request_data,response_data,response_headers);
      ArrayInitialize(request_data,0);
      ArrayInitialize(body_bytes,0);
      if(status>=200 && status<300)
         return true;

      if(status==-1)
         PrintFormat("GoldAiTrader bridge request %s failed locally (error %d).",
                     path,GetLastError());
      else
         PrintFormat("GoldAiTrader bridge request %s was rejected with HTTP %d.",path,status);
      return false;
     }
  };

#endif
