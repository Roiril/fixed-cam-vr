"""Separate intended background dimming from scalar occlusion in the OFF comparison."""
import numpy as np
def smooth(a,b,t):
    x=np.clip((t-a)/(b-a),0,1);return x*x*(3-2*x)
def occlusion_fraction(actual,control,t):
    dissolve=smooth(11.5,12.7,t);contrast=1-.32*dissolve
    light=(1-.5*smooth(11.5,12.4,t))*(1-.58*smooth(12.4,12.7,t))
    # Reentry uses an explicit shader gate; background dimming no longer applies.
    if t>=13.28:contrast=1.;light=1.
    expected=(control*contrast+.045*255*(1-contrast))*light
    valid=control>32
    return float(((actual<expected*.5)&valid).sum()/max(1,valid.sum()))
