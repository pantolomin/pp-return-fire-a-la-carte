# pp-return-fire-a-la-carte

Allows you to change RETURN FIRE the way you prefer it.
By default return fire is hanged as follows:
- 1 return fire only per enemy
- everyone can retaliate, not just the target and casualties
- 120° angle instead of 360°

Be aware that the angle uses the direction the target is watching (like infiltrator's shock perk). Arthrons tend to look slightly on their left so don't focus on how their feet are positioned.

#### use the pp-return-fire-a-la-carte.properties file to configure as you please
```
# Defines how many return fire shots are allowed per "actor"
# Set to 0 for unlimited
ShotLimit = 1

# Change the perception ratio that triggers RF
# In-game value defaults to 0.5 ( half perception )
# Setting at 0 disables RF
# ( Stealth counts, so if your soldier has 50% stealth, a ratio of 0.5 forces RF to use only 0.25 of the shooter's perception )
PerceptionRatio = 0.5

# Defines if you allow a "bash" riposte
# I guess it's related to the torso mutation with 4 tentacles
# ( if one day they add an enemy that uses it and it annoys you )
AllowBashRiposte = true

# Defines if the target(s) of your attack can retaliate
TargetCanRetaliate = true

# Defines if the casualties you made can retaliate
# ( if you shot on a Triton and accidentaly have a bullet hitting an Arthron )
CasualtiesCanRetaliate = true

# Defines if anyone else in range can retaliate
# ( neither a target or a casualty )
BystandersCanRetaliate = true

# Defines if retaliation aborts for risk of friendly fire
# ( the game currently checks for this, but if you want to have fun seeing Arthrons retaliate on other Pandorans ... )
CheckFriendlyFire = true

# Defines the angle in which return fire can trigger
# Between 0° and 360°
# The game currently uses a 360° angle (no check ;o))
ReactionAngle = 120

# I hear you jump and shout for this next one ...
# ... but haven't yet found how to make it work
# AllowReturnToCover = true
```

**Special thanks to Cally Ceistigh & Karin Winnicki who have died countless times to 50 damage machine guns return fire during the tests.**
