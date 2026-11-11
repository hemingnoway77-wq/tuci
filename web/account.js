export const apiRoot = "/tuci-sync/v1/";
let originProof;
let pendingOriginProof;

const currentOriginProof = () => originProof?.until > Date.now() ? originProof.value : null;
const aborted = () => new DOMException('请求已取消。', 'AbortError');

async function loadOriginProof(signal) {
  const response = await fetch(apiRoot + "account/origin-proof", {
    method: "GET", credentials: "same-origin", mode: "same-origin", referrerPolicy: "same-origin",
    cache: "no-store", redirect: "error", signal
  });
  if (!response.ok) return null;
  const reply = await response.json().catch(() => null);
  if (typeof reply?.proof !== "string" || !/^[A-Za-z0-9_-]{96}$/.test(reply.proof) ||
      !Number.isSafeInteger(reply.expiresIn) || reply.expiresIn < 1 || reply.expiresIn > 120) return null;
  originProof = { value: reply.proof, until: Date.now() + reply.expiresIn * 1000 - 500 };
  return originProof.value;
}

function requestOriginProof(signal) {
  if (signal?.aborted) return Promise.reject(aborted());
  if (!pendingOriginProof) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 20000);
    pendingOriginProof = loadOriginProof(controller.signal).finally(() => {
      clearTimeout(timeout);
      pendingOriginProof = undefined;
    });
  }
  if (!signal) return pendingOriginProof;
  return new Promise((resolve, reject) => {
    const abort = () => reject(aborted());
    signal.addEventListener('abort', abort, {once:true});
    pendingOriginProof.then(value => {
      signal.removeEventListener('abort', abort);
      resolve(value);
    }, error => {
      signal.removeEventListener('abort', abort);
      reject(error);
    });
  });
}

export function requiresOriginProof(response) {
  return response.status === 403 && response.headers?.get?.("X-Tuci-Origin-Error") === "proof-required";
}

export async function fetchWithOriginProof(url, options) {
  if (!url.startsWith(apiRoot) || options.method !== "POST" || options.credentials !== "same-origin") return fetch(url, options);
  const send = proof => fetch(url, proof ? {
    ...options, headers: { ...options.headers, "X-Tuci-Origin-Proof": proof }
  } : options);
  const sent = currentOriginProof();
  const first = await send(sent);
  if (!requiresOriginProof(first)) return first;
  if (originProof?.value === sent) originProof = undefined;
  const proof = currentOriginProof() ?? await requestOriginProof(options.signal);
  if (!proof) return first;
  void first.body?.cancel().catch(() => {});
  return send(proof);
}

export function checkedIdentity(value) {
  if (!value || !["userId","vaultId","username","deviceId"].every(key => typeof value[key] === "string" && value[key].length > 0))
    throw new Error("账号服务返回了无法识别的数据。");
  return {userId:value.userId,vaultId:value.vaultId,username:value.username,deviceId:value.deviceId,canInvite:value.canInvite===true};
}
export async function accountRequest(route,body,legacyToken,expectedVault,{signal}={}) {
  return request("account/"+route,body,legacyToken,expectedVault,{signal});
}
// Registration invites live beside the account routes; the same timeout, cookie and
// error handling applies, and a null body performs a GET.
export async function invitesRequest(body,expectedVault,{signal}={}) {
  return request("invites",body===null?undefined:body,null,expectedVault,{signal});
}
async function request(path,body,legacyToken,expectedVault,{signal}={}) {
  const headers={};
  if (body!==undefined) headers["Content-Type"]="application/json";
  if (legacyToken) headers.Authorization="Bearer "+legacyToken;
  if (expectedVault) headers["X-Tuci-Vault"]=expectedVault;
  const controller=new AbortController(),abort=()=>controller.abort(),timeout=setTimeout(()=>controller.abort(),20000);
  if(signal?.aborted){clearTimeout(timeout);throw new DOMException('请求已取消。','AbortError');}
  signal?.addEventListener('abort',abort,{once:true});
  try {
    const response=await fetchWithOriginProof(apiRoot+path,{method:body===undefined?"GET":"POST",headers,
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
