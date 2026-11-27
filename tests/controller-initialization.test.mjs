import assert from 'node:assert/strict';
import fs from 'node:fs';

const template=fs.readFileSync(new URL('../csharp/src/Wallpaper2Video.Runtime.Windows/Resources/particle-controller.js',import.meta.url),'utf8');
function controller(clocks,layer){
  const events=[];
  const source=template.replaceAll('ENTRIES','[]').replaceAll('CLOCKS',JSON.stringify(clocks)).replaceAll('OVERLAP','1')
    .replaceAll('KEY','"test-key"').replaceAll('PREFIX','"TEST: "').replaceAll('export function ','function ');
  const engine={userProperties:{'test-key':0},frametime:1/60};
  const api=new Function('engine','thisScene','console',source+'\nreturn {update,init};')(engine,{getLayerByID:()=>layer},{error:s=>events.push(JSON.parse(s.slice(6)))});
  return {api,events};
}
const missing=controller([{id:'411',kind:'timeline',name:'missing',seconds:6}],{getAnimation:()=>null});
assert.doesNotThrow(()=>missing.api.update(true),'A native lookup failure must emit a job-scoped diagnostic instead of escaping update');
assert.equal(missing.events[0]?.stage,'initialization_error');
assert.match(missing.events[0]?.error,/411\/timeline\/missing/);
missing.api.update(true);
assert.equal(missing.events.length,1,'Failed initialization must emit one diagnostic');
console.log('PASS native initialization failure is explicit, scoped and emitted once');
