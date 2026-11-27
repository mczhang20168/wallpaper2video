'use strict';
const entries = ENTRIES;
const clocks = CLOCKS;
const overlap = OVERLAP;
const key = KEY;
const prefix = PREFIX;
const warmAssets=entries.map(e=>e.warm>0?engine.registerAsset(e.config.particle):null);
let roots = [], timers = [], rates = [], alphas = [];
let armed = false, initialized = false, ready = false, verified = false, drained = false;
let initializationFailed = false;
let elapsed = 0;
function trace(stage) {
    console.error(prefix + JSON.stringify({stage:stage,elapsed:elapsed,
        active:roots.map(p=>p.isPlaying()),clocks:timers.map(t=>t.spec.kind==='video'?t.player.getCurrentTime():t.player.getFrame()),rates:timers.map(t=>t.player.rate),durations:timers.map(t=>t.player.duration)}));
}
function readProperty(name) {
    let v=engine.userProperties[name];
    return v && typeof v==='object'?v.value:v;
}
function prepare() {
    roots=entries.map(e=>thisScene.getLayerByID(e.id));
    if(roots.some(p=>!p))return false;
    timers=clocks.map(c=>{
        let layer=thisScene.getLayerByID(c.id);
        let owner=layer;
        if(c.effect_index!==undefined){owner=layer.getEffect(c.effect_index);if(owner && c.material_index!==undefined)owner=owner.getMaterial(c.material_index);}
        let player=c.kind==='video'?layer.getVideoTexture():c.kind==='sprite'?layer.getTextureAnimation():c.kind==='bone'?layer.getAnimationLayer(c.index):owner&&owner.getAnimation(c.name);
        if(!player)throw new Error('Export clock missing: '+c.id+'/'+c.kind+'/'+c.name);
        if(c.kind==='sprite' && (!player.isPlaying() || player.frameCount!==c.frame_ends.length ||
            Math.abs(player.duration-c.frame_ends[c.frame_ends.length-1])>Math.max(.001,player.duration*.0001)))
            throw new Error('序列贴图原生播放状态与已解析时序不一致：'+c.id);
        return {spec:c,player:player,started:false};
    });
    timers.forEach(t=>{
        if(t.spec.kind==='video'){t.player.pause();t.player.loop=true;}
        else if(t.spec.kind==='sprite'){t.player.pause();t.player.setFrame(0);return;}
        else t.player.stop();
        t.player.rate=t.spec.rate;
    });
    roots.forEach((p,i)=>{
        rates[i]=entries[i].rate;alphas[i]=entries[i].alpha;
        p.instance.rate=rates[i];p.instance.alpha=alphas[i];p.stop();
    });
    initialized=true;return true;
}
function startClock(t,offset) {
    const c=t.spec,p=t.player;
    if(c.kind!=='sprite')p.rate=c.rate;
    if(c.single) {
        p.setFrame(Math.max(0,offset-overlap)*p.fps);p.play();t.started=true;return;
    }
    if(c.kind==='video') {
        p.rate=1;p.play();
    } else {p.pause();setPose(t,offset);}
    t.started=true;
}
function setPose(t,offset) {
    const c=t.spec;
    const initial=(c.start_phase||0)*(c.mirror ? 0.5 : 1);
    const phase=((((offset-overlap)/c.seconds)*(c.rate<0?-1:1)+initial)%1+1)%1;
    if(c.kind==='sprite'){
        const ends=c.frame_ends,time=phase*ends[ends.length-1];
        let lo=0,hi=ends.length-1;
        while(lo<hi){const mid=(lo+hi)>>1;if(time<ends[mid])hi=mid;else lo=mid+1;}
        if(lo!==t.player.getFrame())t.player.setFrame(lo);
        if(lo!==t.player.getFrame() || t.player.isPlaying()){
            initializationFailed=true;
            reportClockFailure('序列贴图定位未生效：'+c.id);
        }
        return;
    }
    let frame=c.mirror?(phase<0.5?phase*2:(1-phase)*2)*c.frames:phase*c.frames;
    t.player.setFrame(frame);
}
function reportClockFailure(error){
    console.error(prefix+JSON.stringify({stage:'initialization_error',error:String(error)}));
}
export function init(value) { return value; }
export function update(value) {
    if(initializationFailed)return value;
    if(!initialized){
        try{if(!prepare())return value;}
        catch(error){initializationFailed=true;reportClockFailure(error);return value;}
    }
    if(!ready) {
        ready=true;trace('ready');
    }
    const signal=readProperty(key);
    if(signal===1 && !armed) {
        armed=true;elapsed=readProperty(key+'_offset')||0;
        timers.forEach(t=>{if(!t.spec.single)startClock(t,elapsed);});trace('armed');
    }
    if(!armed)return value;
    elapsed+=engine.frametime;
    timers.forEach(t=>{
        if(t.spec.single && !t.started && elapsed>=overlap)startClock(t,elapsed);
        else if(!t.spec.single && t.spec.kind!=='video')setPose(t,elapsed);
    });
    for(let i=0;i<roots.length;i++) {
        const e=entries[i];let p=roots[i];
        if(e.stage===0 && e.disabled){p.stop();e.stage=2;trace('emission_skipped');}
        else if(e.stage===0 && elapsed>=e.stop){p.stop();e.stage=2;trace(e.allowEmpty?'window_elapsed':'missed_window');}
        else if(e.stage===0 && elapsed>=e.start){
            if(e.warm>0){
                const index=thisScene.getLayerIndex(p);
                const config=JSON.parse(JSON.stringify(e.config));
                config.particle=warmAssets[i];
                p=thisScene.createLayer(config);thisScene.sortLayer(p,index);roots[i]=p;
            } else {p.instance.rate=rates[i];p.instance.alpha=alphas[i];p.play();}
            e.stage=1;trace('emitting');
        }
        if(e.stage===1 && elapsed>=e.stop && !e.natural){p.pause();e.stage=2;trace('paused');}
    }
    if(!drained && entries.every(e=>!e.safe || (e.stage===2 && (e.disabled || elapsed>=e.stop+e.drain)))){drained=true;trace('drain_wait_completed');}
    if(signal===2 && !verified){verified=true;trace('verify');}
    return value;
}
