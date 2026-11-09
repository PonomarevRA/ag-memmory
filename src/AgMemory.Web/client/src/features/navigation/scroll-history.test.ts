import {expect,it,vi} from 'vitest';
import {initialize,beginNavigation,trackVisit,dispose} from './index.js';

it('retains outgoing reading position while a new destination starts at the top',()=>{
  let y=500;
  const original=Object.getOwnPropertyDescriptor(window,'scrollY');
  Object.defineProperty(window,'scrollY',{configurable:true,get:()=>y});
  const scrolling=vi.spyOn(window,'scrollTo').mockImplementation((left:any,top?:number)=>{y=typeof left==='object'?left.top:top!;});
  const frame=vi.spyOn(window,'requestAnimationFrame').mockImplementation(callback=>{callback(0);return 1;});
  history.replaceState(null,'','/knowledge');initialize('/knowledge');
  try {
    beginNavigation('/about');history.pushState(null,'','/about');
    window.scrollTo({left:0,top:0,behavior:'instant'});
    window.dispatchEvent(new Event('scroll'));trackVisit('/about','About');
    expect(y).toBe(0);
    history.replaceState(null,'','/knowledge');window.dispatchEvent(new PopStateEvent('popstate'));
    expect(y).toBe(500);
  } finally {
    dispose();scrolling.mockRestore();frame.mockRestore();
    if(original)Object.defineProperty(window,'scrollY',original);
  }
});
