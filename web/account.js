export const apiRoot = "/tuci-sync/v1/";
export function checkedIdentity(value) {
  if (!value || !["userId","vaultId","username","deviceId"].every(key => typeof value[key] === "string" && value[key].length > 0))
    throw new Error("账号服务返回了无法识别的数据。");
  return {userId:value.userId,vaultId:value.vaultId,username:value.username,deviceId:value.deviceId};
}
export async function accountRequest(route,body,legacyToken,expectedVault,{signal}={}) {
  const headers={};
  if (body!==undefined) headers["Content-Type"]="application/json";
  if (legacyToken) headers.Authorization="Bearer "+legacyToken;
  if (expectedVault) headers["X-Tuci-Vault"]=expectedVault;
  const controller=new AbortController(),abort=()=>controller.abort(),timeout=setTimeout(()=>controller.abort(),20000);
  if(signal?.aborted){clearTimeout(timeout);throw new DOMException('请求已取消。','AbortError');}
  signal?.addEventListener('abort',abort,{once:true});
  try {
    const response=await fetch(apiRoot+"account/"+route,{method:body===undefined?"GET":"POST",headers,
      body:body===undefined?undefined:JSON.stringify(body),credentials:"same-origin",mode:"same-origin",
      referrerPolicy:"same-origin",cache:"no-store",redirect:"error",signal:controller.signal});
    const value=await response.json().catch(()=>({}));
    if (!response.ok) {const error=new Error(value.error || "账号操作暂时无法完成（"+response.status+"）。");error.status=response.status;throw error;}
    return value;
  } catch(error) {
    if(signal?.aborted)throw new DOMException('请求已取消。','AbortError');
    if (error instanceof TypeError || error.name==="AbortError") throw new Error("无法连接账号服务，请检查网络后重试。");
    throw error;
  } finally {clearTimeout(timeout);signal?.removeEventListener('abort',abort);}
}
export const themeNames={warm:"warmNeutral",ink:"mono",rose:"pink"};
export const localThemes={warmNeutral:"warm",mono:"ink",pink:"rose"};
