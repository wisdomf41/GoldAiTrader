#ifndef GOLDAITRADER_BRIDGE_CRYPTO_MQH
#define GOLDAITRADER_BRIDGE_CRYPTO_MQH

bool GatUtf8Bytes(const string value,uchar &bytes[])
  {
   ArrayFree(bytes);
   int copied=StringToCharArray(value,bytes,0,WHOLE_ARRAY,CP_UTF8);
   if(copied<=0)
      return StringLen(value)==0;
   int size=ArraySize(bytes);
   if(size>0 && bytes[size-1]==0)
      ArrayResize(bytes,size-1);
   return true;
  }

string GatHex(const uchar &bytes[])
  {
   string value="";
   for(int index=0;index<ArraySize(bytes);index++)
      value+=StringFormat("%02x",(uint)bytes[index]);
   return value;
  }

bool GatSha256(const uchar &data[],uchar &digest[])
  {
   uchar empty_key[];
   ArrayResize(empty_key,0);
   ArrayFree(digest);
   ResetLastError();
   int size=CryptEncode(CRYPT_HASH_SHA256,data,empty_key,digest);
   return size==32 && ArraySize(digest)==32;
  }

bool GatSha256Hex(const uchar &data[],string &hex)
  {
   uchar digest[];
   if(!GatSha256(data,digest))
      return false;
   hex=GatHex(digest);
   ArrayInitialize(digest,0);
   ArrayFree(digest);
   return true;
  }

bool GatHmacSha256(const uchar &key[],const uchar &data[],uchar &result[])
  {
   uchar normalized_key[];
   if(ArraySize(key)>64)
     {
      if(!GatSha256(key,normalized_key))
         return false;
     }
   else
     {
      ArrayResize(normalized_key,ArraySize(key));
      ArrayCopy(normalized_key,key);
     }

   uchar inner_pad[],outer_pad[];
   ArrayResize(inner_pad,64);
   ArrayResize(outer_pad,64);
   ArrayInitialize(inner_pad,0x36);
   ArrayInitialize(outer_pad,0x5c);
   for(int index=0;index<ArraySize(normalized_key);index++)
     {
      inner_pad[index]=(uchar)(normalized_key[index]^0x36);
      outer_pad[index]=(uchar)(normalized_key[index]^0x5c);
     }

   uchar inner_input[];
   ArrayResize(inner_input,64+ArraySize(data));
   ArrayCopy(inner_input,inner_pad,0,0,64);
   ArrayCopy(inner_input,data,64,0,ArraySize(data));
   uchar inner_hash[];
   if(!GatSha256(inner_input,inner_hash))
      return false;

   uchar outer_input[];
   ArrayResize(outer_input,64+ArraySize(inner_hash));
   ArrayCopy(outer_input,outer_pad,0,0,64);
   ArrayCopy(outer_input,inner_hash,64,0,ArraySize(inner_hash));
   bool success=GatSha256(outer_input,result);

   ArrayInitialize(normalized_key,0);
   ArrayInitialize(inner_pad,0);
   ArrayInitialize(outer_pad,0);
   ArrayInitialize(inner_input,0);
   ArrayInitialize(inner_hash,0);
   ArrayInitialize(outer_input,0);
   return success;
  }

bool GatHmacSha256Hex(const string secret,const string canonical,string &hex)
  {
   uchar key[],data[],digest[];
   if(!GatUtf8Bytes(secret,key) || !GatUtf8Bytes(canonical,data) ||
      !GatHmacSha256(key,data,digest))
      return false;
   hex=GatHex(digest);
   ArrayInitialize(key,0);
   ArrayInitialize(data,0);
   ArrayInitialize(digest,0);
   return true;
  }

string GatCreateNonce(const string secret,const string bridge_id,const string account_id,
                      ulong &counter)
  {
   counter++;
   string material=bridge_id+"\n"+account_id+"\n"+
                   StringFormat("%I64u\n%I64u\n%I64d\n%d",
                                GetMicrosecondCount(),counter,ChartID(),
                                (int)TerminalInfoInteger(TERMINAL_BUILD));
   string nonce="";
   if(!GatHmacSha256Hex(secret,material,nonce))
      return "";
   return nonce;
  }

#endif
