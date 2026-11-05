// Only a section whose original program-generated hash still matches may change.
const stop = new Set(("a an the and or but so because if when while as of in on at to for from by with without into over under about is am are was were be been being do does did have has had can could may might must will would shall should i me my mine we us our ours you your yours he him his she her hers it its they them their theirs this that these those not no yes very too also just then than there here who what which where why how all any each every some such own same up down out off again only even still more most much many few less other another both either neither").split(" "));

function documentLines(text) {
  const lines = [];
  let fence = "", comment = false;
  for (const match of text.matchAll(/[^\r\n]*(?:\r\n|\r|\n|$)/g)) {
    if (!match[0]) break;
    const body = match[0].replace(/[\r\n]+$/, "");
    const marker = /^ {0,3}(`{3,}|~{3,})(.*)$/.exec(body);
    const visible = !fence && !comment && !marker;
    if (fence) {
      if (marker && marker[1][0] === fence[0] && marker[1].length >= fence.length && !marker[2].trim()) fence = "";
    } else if (!comment && marker) {
      fence = marker[1];
    } else {
      for (const tag of body.matchAll(/<!--|-->/g)) {
        if (tag[0] === "<!--") comment = true;
        else comment = false;
      }
    }
    const header = visible ? /^(#{1,6})[ \t]+(.*?)[ \t]*$/.exec(body) : null;
    lines.push({start:match.index, body, visible, level:header?.[1].length, title:header?.[2]});
  }
  return lines;
}

function sections(text) {
  const lines = documentLines(text), found = [];
  for (const [name,label] of [["examples","例句"],["relations","语义关系"],["roots","词根"],["forms","词形"]]) {
    const heading = name === "examples" || name === "relations";
    const prefix = "**" + label + "**";
    const starts = lines.filter(line => line.visible && (heading ? line.level === 4 && line.title === label :
      line.body.startsWith(prefix + "：") || line.body.startsWith(prefix + ":")));
    if (starts.length !== 1) continue;
    const start = starts[0].start;
    const end = lines.find(line => line.start > start &&
      (heading ? line.level <= 4 : !line.body.trim() || line.level))?.start ?? text.length;
    found.push({name, start, text:text.slice(start,end)});
  }
  return found.sort((a,b) => b.start - a.start);
}

async function digest(text) {
  const normalized = text.replace(/\r\n?/g,"\n").replace(/\n+$/,"");
  const bytes = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(normalized));
  return Array.from(new Uint8Array(bytes), b => b.toString(16).padStart(2,"0")).join("");
}

export async function regionHashes(markdown) {
  return Object.fromEntries(await Promise.all(sections(markdown).map(async section =>
    [section.name, await digest(section.text)])));
}

function title(path) {
  return path.split("/").pop().replace(/\.md$/i,"").replace(/^_(con|aux|prn|nul)$/i,"$1").toLowerCase();
}
export function linkTarget(path) {
  const name = title(path);
  if (path.startsWith("词根/")) return {group:"roots", term:name.replace(/^-+|-+$/g,"")};
  if (name.length <= 64 && /^[a-z]+(?:[-'][a-z]+)*$/.test(name)) return {group:"words", term:name};
  return null;
}

export function linkIndex(notes) {
  const index = {roots:new Map(), words:new Map()};
  for (const note of notes) {
    const target = linkTarget(note.path);
    if (!target) continue;
    const group = index[target.group];
    if (!group.has(target.term)) group.set(target.term, []);
    group.get(target.term).push(note.path);
  }
  return index;
}

function rewrite(line, mode, ownWord, index) {
  const relStart = /^\s*-\s+/.exec(line);
  if ((mode === "examples" && !/^\s*\d+\.\s+/.test(line)) || (mode === "relations" && !relStart)) return line;

  const previous = new Map();
  // Recognize links written by the app (filename equals displayed word or root
  // variant), keeping prefixed Obsidian links byte-for-byte when still valid.
  const strip = text => text.replace(/`[^`\n]*`|\[[^\[\]\n]*\]\([^\n)]*\)|https?:\/\/\S+|\\.|\[\[([^\[\]\n|]+)\|([A-Za-z][A-Za-z'’/ -]*)\]\]/g, (whole, dest, label) => {
    if (!dest) return whole;
    const stem = dest.split("/").pop().replace(/^[_-]+|[_-]+$/g,"").toLowerCase();
    if (!label.toLowerCase().split("/").some(part => part.trim() === stem)) return whole;
    previous.set(label, {dest:dest.toLowerCase(), whole});
    return label;
  });

  function link(path, label) {
    if (/[#^\[\]|]/.test(path)) return label;
    const dest = path.slice(0,-3);
    const old = previous.get(label);
    if (old && (old.dest === dest.toLowerCase() || old.dest.endsWith("/" + dest.toLowerCase()))) return old.whole;
    return "[[" + dest + "|" + label + "]]";
  }

  if (mode === "roots") {
    const pattern = /(^\*\*词根\*\*[：:]|）[ \t]*；)([ \t]*)(\[\[[^\[\]\n|]+\|[A-Za-z][A-Za-z /-]*\]\]|[A-Za-z]+(?:[ \t]*\/[ \t]*[A-Za-z]+)*)(?=（)/g;
    return line.replace(pattern,(whole, separator, space, group) => {
      const label = strip(group);
      if (!/^[A-Za-z]+(?:[ \t]*\/[ \t]*[A-Za-z]+)*$/.test(label)) return whole;
      const matches = new Set(label.toLowerCase().split("/").flatMap(part => index.roots.get(part.trim()) || []));
      return separator + space + (matches.size === 1 ? link([...matches][0], label) : label);
    });
  }

  const plain = strip(line);
  const used = new Set();
  const tokens = /\[\[[^\n]*?\]\]|\[[^\[\]\n]*\]\([^\n)]*\)|`[^`\n]*`|https?:\/\/\S+|(?<![A-Za-z0-9_])[A-Za-z]+(?:[-'’][A-Za-z]+)*(?![A-Za-z0-9_])/g;
  return plain.replace(tokens, (token, offset) => {
    const key = token.toLowerCase().replaceAll("’","'");
    const candidates = index.words.get(key);
    if (key === ownWord || candidates?.length !== 1) return token;
    if (mode === "relations" && (offset !== relStart[0].length || !/^\s*[:：]/.test(plain.slice(offset+token.length)))) return token;
    if (mode === "examples") {
      if (stop.has(key) || used.size >= 3 || used.has(key)) return token;
      used.add(key);
    }
    return link(candidates[0],token);
  });
}

// The complete note is a compare-and-swap input for the storage transaction.
export async function refreshManaged(note, index) {
  if (!note.regions || !Object.keys(note.regions).length) return null;
  const regions = {...note.regions};
  let markdown = note.markdown;
  for (const section of sections(note.markdown)) {
    const owned = regions[section.name]?.toLowerCase();
    if (!owned) continue;
    let newText = section.text;
    for (const line of documentLines(section.text).reverse()) {
      if (!line.visible || line.body.includes("<!--")) continue;
      const rewritten = rewrite(line.body, section.name, title(note.path), index);
      if (line.body !== rewritten)
        newText = newText.slice(0,line.start) + rewritten + newText.slice(line.start + line.body.length);
    }
    if (newText === section.text || await digest(section.text) !== owned) continue;
    markdown = markdown.slice(0,section.start) + newText + markdown.slice(section.start + section.text.length);
    regions[section.name] = await digest(newText);
  }
  return markdown === note.markdown ? null : {markdown,regions};
}
