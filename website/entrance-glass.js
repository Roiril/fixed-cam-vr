/* Web adaptation of IntroFractureTime.hlsl and IntroFracture.shader.
 * The source image is the title view. Landed pieces reveal the real DOM beneath
 * the canvas, so every aspect ratio finishes on the exact interactive page.
 */
(() => {
  'use strict';
  const vertexSource = `
    precision highp float;
    attribute vec3 aLocal;
    attribute vec3 aNormal;
    attribute vec4 aPiece;
    attribute vec4 aMacro;
    attribute vec4 aEdges;
    attribute float aSurface;
    uniform float uProgress;
    uniform vec2 uAspect;
    uniform float uMask;
    varying vec3 vPhoto;
    varying vec3 vWorld;
    varying vec3 vNormal;
    varying vec4 vEdges;
    varying vec4 vLight;
    varying vec4 vDetail;
    varying float vSurface;
    varying float vReveal;
    float sat(float x) { return clamp(x, 0.0, 1.0); }
    float ease(float a, float b, float x) {
      float t = sat((x-a)/(b-a));
      return t*t*t*(t*(t*6.0-15.0)+10.0);
    }
    vec2 hash2(vec2 p, float seed) {
      vec3 q = fract(p.xyx*vec3(.1031,.1030,.0973)+seed);
      q += dot(q,q.yzx+33.33);
      return fract((q.xx+q.yz)*q.zy);
    }
    vec3 spin(vec3 v, vec3 axis, float angle) {
      return v*cos(angle)+cross(axis,v)*sin(angle)+axis*dot(axis,v)*(1.0-cos(angle));
    }
    vec3 shell(vec2 p) { return normalize(vec3(p/.15*uAspect,1.0))*1.6; }
    vec3 target(vec2 p) { return vec3(p/.15*uAspect*1.8,1.8); }
    void main() {
      float p = uProgress;
      vec2 center = aPiece.xy;
      float size = aPiece.z;
      float closer = aPiece.w;
      vec2 noise = hash2(center,aMacro.w+11.0);
      vec2 noise2 = hash2(center+.37,aMacro.w+29.0);
      vec2 macroNoise = hash2(vec2(aMacro.w,17.0),3.7);
      float rank = sat((size-.016)/.039);
      float largePiece = smoothstep(.016,.055,size);
      float weight = sat(.55*rank+.25*sat(length(center)/.15)+.20*noise2.y);
      float arrive = mix(.70+.17*pow(weight,.6),.90,closer);
      float pullPow = mix(1.6,3.5,max(weight,closer));
      float pullT = sat((p-.56)/(arrive-.56));
      float travel = pow(pullT,pullPow);
      float reveal = ease(arrive,arrive+.024,p);
      vReveal = reveal;
      vec2 uv = aLocal.xy/.6+.5;
      vPhoto = vec3(uv,1.0);
      vEdges = aEdges;
      vSurface = aSurface;
      if (uMask > .5) {
        gl_Position = vec4(aLocal.xy/.30,0.0,1.0);
        vWorld=vec3(0.0); vNormal=vec3(0.0,0.0,-1.0);
        vLight=vec4(0.0); vDetail=vec4(0.0);
        return;
      }
      vec3 rawVertex = shell(aLocal.xy);
      vec3 rawCenter = shell(center);
      vec3 macroCenter = shell(aMacro.xy);
      vec3 macroRay = normalize(macroCenter);
      vec3 tangentX = normalize(vec3(1.0,0.0,-macroRay.x/max(macroRay.z,.0001)));
      vec3 tangentY = normalize(cross(macroRay,tangentX));
      vec2 originalSlope = center/.15;
      vec2 fromOrigin = originalSlope-vec2(-.16,.12);
      float originDist = sat(length(fromOrigin)/2.2);
      vec2 radial = normalize(fromOrigin);
      vec2 tangent = vec2(-radial.y,radial.x);
      float breakAt = .060+aMacro.z*(.040/.14)+noise.x*.004;
      float crack = ease(breakAt,breakAt+.006,p);
      float x = max(p-breakAt,0.0);
      float xs = max(p-.52,0.0);
      float u = .80*(1.0-exp(-x/.024))+.60*(x-xs*ease(0.0,.12,xs));
      float spinTime = .80*(1.0-exp(-x/.024))+.60*x;
      float macroAngle = radians(mix(1.5,3.5,macroNoise.y))*(macroNoise.x<.5?-1.0:1.0)*crack;
      vec3 macroAxis = normalize(mix(tangentX,tangentY,macroNoise.x));
      vec3 peel = macroRay*.018*crack;
      vec3 sourceVertex = macroCenter+spin(rawVertex-macroCenter,macroAxis,macroAngle)+peel;
      vec3 sourceCenter = macroCenter+spin(rawCenter-macroCenter,macroAxis,macroAngle)+peel;
      float lateral = (mix(.34,.58,noise.x)+size*.7)*mix(1.15,.85,rank);
      float depth = mix(.30,-.14,largePiece)+smoothstep(.35,1.25,length(originalSlope))*.26+(noise.y-.5)*.14;
      vec3 burst = vec3(radial*lateral+tangent*(noise2.x-.5)*.14,depth);
      vec3 flightCenter = sourceCenter+burst*u;
      vec3 targetCenter = target(center);
      vec3 worldCenter = mix(flightCenter,targetCenter,travel);
      vec3 pullPath = targetCenter-flightCenter;
      float pullDistance = length(pullPath);
      vec3 pullDir = pullPath/max(pullDistance,.0001);
      float pullRate = pullPow*pow(max(pullT,.0001),pullPow-1.0)/(arrive-.56);
      float speed = pullRate*pullDistance/5.0*step(.0001,1.0-travel);
      float stretch = .55*sat(speed/2.0)*(1.0-ease(.92,1.0,travel))*step(.56,p);
      float tiltRate = radians(mix(52.0,18.0,rank)*(.55+.9*noise2.y))*(noise.x<.5?-1.0:1.0);
      float rollRate = radians(mix(38.0,13.0,rank)*(.55+.9*noise2.x))*(noise.y<.5?-1.0:1.0);
      vec3 travelAxis = normalize(mix(tangentX,vec3(1.0,0.0,0.0),travel));
      vec3 faceAxis = normalize(mix(macroRay,vec3(0.0,0.0,1.0),travel));
      float spinAmount = spinTime*(1.0-travel);
      vec3 relative = mix(sourceVertex-sourceCenter,target(aLocal.xy)-targetCenter,travel);
      relative=spin(relative,travelAxis,tiltRate*spinAmount);
      relative=spin(relative,faceAxis,rollRate*spinAmount);
      relative+=pullDir*dot(relative,pullDir)*stretch;
      float detail=crack*(1.0-travel);
      vec3 faceNormal=spin(spin(faceAxis,travelAxis,tiltRate*spinAmount),faceAxis,rollRate*spinAmount);
      float thickness=mix(.006,.015,largePiece)*detail;
      vec3 world=worldCenter+relative+faceNormal*aLocal.z*thickness;
      vec3 pieceRay=normalize(rawCenter);
      vec3 sourceX=normalize(vec3(1.0,0.0,-pieceRay.x/max(pieceRay.z,.0001)));
      vec3 sourceY=normalize(cross(pieceRay,sourceX));
      vec3 normal=normalize(mix(sourceX*aNormal.x+sourceY*aNormal.y+pieceRay*aNormal.z,aNormal,travel));
      normal=spin(spin(normal,travelAxis,tiltRate*spinAmount),faceAxis,rollRate*spinAmount);
      float glowOn=.008+originDist*.040;
      float glow=ease(glowOn,glowOn+.010,p)*(.40+.60*(1.0-ease(glowOn+.010,glowOn+.030,p)))*(1.0-ease(.058,.072,p));
      float shock=ease(.056,.064,p)*(1.0-ease(.064,.105,p));
      float breakLight=ease(breakAt,breakAt+.005,p)*(1.0-ease(breakAt+.005,breakAt+.040,p));
      float landing=ease(arrive-.004,arrive,p)*(1.0-ease(arrive,arrive+.010,p));
      float slam=ease(.896,.900,p)*(1.0-ease(.900,.930,p));
      vLight=vec4(glow,breakLight,landing,ease(.11,.20,p)*(1.0-.45*travel)*step(breakAt,p));
      vDetail=vec4(detail,travel,max(shock,slam*.7),sat((length(worldCenter)-1.7)/2.2)*detail);
      vWorld=world;
      vNormal=normal;
      float photoQ=mix(rawVertex.z,1.0,travel);
      vPhoto=vec3(uv*photoQ,photoQ);
      // Camera looks along +Z. w=Z gives correct perspective interpolation.
      gl_Position=vec4(world.xy/(2.0*uAspect),((8.0+.1)/(8.0-.1))*world.z-(2.0*8.0*.1)/(8.0-.1),world.z);
    }
  `;

  function fragmentSource(derivatives) {
    return `${derivatives ? '#extension GL_OES_standard_derivatives : enable' : ''}
    precision highp float;
    uniform sampler2D uTexture;
    uniform vec2 uTexel;
    uniform vec2 uAspect;
    uniform float uProgress;
    uniform float uMask;
    varying vec3 vPhoto;
    varying vec3 vWorld;
    varying vec3 vNormal;
    varying vec4 vEdges;
    varying vec4 vLight;
    varying vec4 vDetail;
    varying float vSurface;
    varying float vReveal;
    float ease(float a,float b,float x) {
      float t=clamp((x-a)/(b-a),0.0,1.0);
      return t*t*t*(t*(t*6.0-15.0)+10.0);
    }
    void main() {
      if(uMask>.5) { gl_FragColor=vec4(0.0,0.0,0.0,vReveal); return; }
      vec2 vUv=vPhoto.xy/max(vPhoto.z,.00001);
      float detail=vDetail.x;
      float ed=min(min(vEdges.x,vEdges.y),min(vEdges.z,vEdges.w));
      float aa=${derivatives ? 'max(fwidth(ed),.000015)' : '.00018'};
      float ridge=1.0-smoothstep(.00012,.00012+aa*1.15,ed);
      float band=exp(-ed/.0011);
      vec3 view=normalize(-vWorld);
      vec3 normal=normalize(vNormal);
      if(dot(normal,view)<0.0) normal=-normal;
      float nv=clamp(dot(normal,view),0.0,1.0);
      float fres=.04+.96*pow(1.0-nv,5.0);
      float grazing=pow(1.0-nv,3.0);
      float bevel=exp(-ed/.00065);
      vec3 ray=refract(-view,normal,.666667);
      vec2 offset=(ray+view).xy*uTexel*(5.0+3.0*bevel)*detail;
      vec3 photo=texture2D(uTexture,vUv).rgb;
      vec3 transmitted=vec3(texture2D(uTexture,vUv+offset*1.12).r,texture2D(uTexture,vUv+offset).g,texture2D(uTexture,vUv+offset*.88).b);
      float lum=dot(transmitted,vec3(.299,.587,.114));
      vec3 body=mix(vec3(lum),transmitted,.24)*(.085+.045*nv)*vec3(1.0,.985,.96);
      vec3 reflected=reflect(-view,normal);
      float coord=dot(reflected,normalize(vec3(.72,.62,.18)))+(vEdges.x-vEdges.y)*3.0;
      float stripAa=${derivatives ? 'max(fwidth(coord),.004)' : '.004'};
      float strip=1.0-smoothstep(.004,.012+stripAa,abs(coord-.22));
      float softStrip=exp(-abs(coord-.22)*26.0)*.025;
      float secondStrip=1.0-smoothstep(.003,.009+stripAa,abs(dot(reflected,normalize(vec3(-.38,.91,.16)))+.36));
      float reflection=(strip*2.4+secondStrip*.85+softStrip)*(.42+.58*fres);
      vec3 white=vec3(1.0,.975,.92);
      vec3 spectrum=.5+.5*cos(6.2831853*(coord*7.0+nv*2.0+bevel*.4+vec3(0.0,.333333,.666667)));
      vec3 edgeColor=mix(white,spectrum,.14*bevel);
      float rim=ridge*(.28+.52*fres)+bevel*(.026+.065*fres);
      float innerEdge=exp(-abs(ed-.0009)/max(aa,.00010))*(.08+.16*fres);
      vec3 polished=body*(1.0-.65*fres)+white*reflection+edgeColor*(rim+innerEdge)+spectrum*(strip*.05+bevel*fres*.04)+white*(.18*vLight.y+.14*vDetail.z+.10*vLight.z);
      if(vSurface>.5) polished=vSurface<1.5?polished*.76:body*.35+edgeColor*(.10+.48*fres)+white*reflection;
      polished=min(polished,vec3(.94))*(1.0-.18*vDetail.w);
      vec3 color=mix(photo,polished,detail);
      color+=vec3(.96,.90,.78)*(ridge*.12+band*.05)*vLight.x;
      float edgeAlpha=clamp(ridge*(.32+.38*grazing)+exp(-ed/.00055)*(.045+.09*grazing),0.0,1.0)*detail;
      edgeAlpha=max(edgeAlpha,clamp(ridge*.72+band*.16,0.0,1.0)*vDetail.y*(1.0-step(.5,vSurface)));
      float radius=length((vUv*2.0-1.0)*uAspect)/length(uAspect);
      float boundary=mix(-.065,1.065,ease(.930,.995,uProgress));
      float radialFade=1.0-ease(radius-.065,radius+.065,boundary);
      if(uProgress>=.995) radialFade=0.0;
      edgeAlpha*=radialFade;
      float photoAlpha=1.0-vReveal;
      // Back and bevel are absent before separation and after docking.
      if(vSurface>.5) photoAlpha*=detail;
      float alpha=edgeAlpha+photoAlpha*(1.0-edgeAlpha);
      vec3 rgb=(edgeColor*edgeAlpha+color*photoAlpha*(1.0-edgeAlpha))/max(alpha,.00001);
      gl_FragColor=vec4(rgb,alpha);
    }`;
  }

  function create(canvas, frozenCanvas) {
    const gl=canvas.getContext('webgl',{alpha:true,antialias:true,depth:true,premultipliedAlpha:false});
    if(!gl || !window.MAWARIMI_FRACTURE_GEOMETRY) throw new Error('Glass renderer unavailable');
    const shaders=[];
    const buffers=[];
    let program;
    let texture;
    function dispose() {
      for(const buffer of buffers) gl.deleteBuffer(buffer);
      for(const shader of shaders) gl.deleteShader(shader);
      if(texture) gl.deleteTexture(texture);
      if(program) gl.deleteProgram(program);
    }
    try {
      for(const [type,source] of [[gl.VERTEX_SHADER,vertexSource],[gl.FRAGMENT_SHADER,fragmentSource(!!gl.getExtension('OES_standard_derivatives'))]]) {
        const shader=gl.createShader(type);
        shaders.push(shader);
        gl.shaderSource(shader,source); gl.compileShader(shader);
        if(!gl.getShaderParameter(shader,gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader));
      }
      program=gl.createProgram();
      shaders.forEach(shader=>gl.attachShader(program,shader)); gl.linkProgram(program);
      if(!gl.getProgramParameter(program,gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));
      texture=gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D,texture);
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL,true);
      gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,gl.RGBA,gl.UNSIGNED_BYTE,frozenCanvas);
      for(const param of [gl.TEXTURE_MIN_FILTER,gl.TEXTURE_MAG_FILTER]) gl.texParameteri(gl.TEXTURE_2D,param,gl.LINEAR);
      for(const param of [gl.TEXTURE_WRAP_S,gl.TEXTURE_WRAP_T]) gl.texParameteri(gl.TEXTURE_2D,param,gl.CLAMP_TO_EDGE);
      const faces=[];
      const solids=[];
      const pieces=window.MAWARIMI_FRACTURE_GEOMETRY.create();
      function vertex(out,point,z,normal,piece,surface) {
        const edges=piece.polygon.map((a,i)=>{
          const b=piece.polygon[(i+1)%piece.polygon.length];
          return Math.abs((b[0]-a[0])*(point[1]-a[1])-(b[1]-a[1])*(point[0]-a[0]))/Math.hypot(b[0]-a[0],b[1]-a[1]);
        });
        while(edges.length<4) edges.push(1);
        out.push(point[0],point[1],z,...normal,...piece.center,piece.size,piece.closer,...piece.macro,...edges.slice(0,4),surface);
      }
      for(const piece of pieces) {
        const polygon=piece.polygon;
        for(let i=1;i<polygon.length-1;i++) {
          for(const p of [polygon[0],polygon[i],polygon[i+1]]) {
            vertex(faces,p,-.5,[0,0,-1],piece,0);
            vertex(solids,p,-.5,[0,0,-1],piece,0);
          }
          for(const p of [polygon[i+1],polygon[i],polygon[0]]) vertex(solids,p,.5,[0,0,1],piece,1);
        }
        for(let i=0;i<polygon.length;i++) {
          const a=polygon[i],b=polygon[(i+1)%polygon.length];
          const len=Math.hypot(b[0]-a[0],b[1]-a[1]);
          const normal=[(b[1]-a[1])/len,(a[0]-b[0])/len,0];
          for(const [p,z] of [[a,-.5],[b,-.5],[b,.5],[a,-.5],[b,.5],[a,.5]]) vertex(solids,p,z,normal,piece,2);
        }
      }
      function upload(data) {
        const buffer=gl.createBuffer(); buffers.push(buffer); gl.bindBuffer(gl.ARRAY_BUFFER,buffer);
        gl.bufferData(gl.ARRAY_BUFFER,new Float32Array(data),gl.STATIC_DRAW);
        return {buffer,count:data.length/19};
      }
      const mask=upload(faces),solid=upload(solids);
      const attributes=[['aLocal',3],['aNormal',3],['aPiece',4],['aMacro',4],['aEdges',4],['aSurface',1]].map(([name,size])=>({location:gl.getAttribLocation(program,name),size}));
      const uniforms=Object.fromEntries(['uProgress','uAspect','uMask','uTexel'].map(name=>[name,gl.getUniformLocation(program,name)]));
      function bind(mesh) {
        gl.bindBuffer(gl.ARRAY_BUFFER,mesh.buffer);
        let offset=0;
        for(const {location,size} of attributes) {
          gl.enableVertexAttribArray(location); gl.vertexAttribPointer(location,size,gl.FLOAT,false,76,offset*4); offset+=size;
        }
      }
      let width=1,height=1;
      function resize() {
        const ratio=Math.min(devicePixelRatio||1,1.75);
        width=Math.max(1,Math.round(innerWidth*ratio)); height=Math.max(1,Math.round(innerHeight*ratio));
        canvas.width=width; canvas.height=height; gl.viewport(0,0,width,height);
      }
      function render(progress) {
        gl.useProgram(program);
        gl.uniform1f(uniforms.uProgress,Math.min(1,Math.max(0,progress)));
        const aspect=Math.sqrt(width/height);
        gl.uniform2f(uniforms.uAspect,aspect,1/aspect);
        gl.uniform2f(uniforms.uTexel,1/frozenCanvas.width,1/frozenCanvas.height);
        gl.clearColor(0,0,0,1); gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);
        gl.enable(gl.BLEND); gl.disable(gl.DEPTH_TEST); gl.depthMask(false);
        // Cut holes in the black backing ONLY after each piece lands.
        gl.blendFunc(gl.ZERO,gl.ONE_MINUS_SRC_ALPHA);
        gl.uniform1f(uniforms.uMask,1); bind(mask); gl.drawArrays(gl.TRIANGLES,0,mask.count);
        gl.uniform1f(uniforms.uMask,0);
        gl.enable(gl.DEPTH_TEST); gl.depthFunc(gl.LEQUAL); gl.depthMask(true);
        gl.blendFuncSeparate(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA,gl.ONE,gl.ONE_MINUS_SRC_ALPHA);
        bind(solid); gl.drawArrays(gl.TRIANGLES,0,solid.count);
      }
      resize();
      return {render,resize,dispose,pieceCount:pieces.length};
    } catch(error) { dispose(); throw error; }
  }
  window.MAWARIMI_GLASS={create,duration:5000};
})();
