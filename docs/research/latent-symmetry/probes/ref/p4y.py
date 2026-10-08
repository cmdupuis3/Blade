import numpy as np
M=np.array([[0.5345,-0.1705,-0.191],[-0.1705,0.3625,0.101],[-0.191,0.101,0.206]])
A=np.array([[0,.4,-.2],[-.4,0,.3],[.2,-.3,0]])
R=A@M+M@A.T; print(repr((R*R).sum()))
G=4*R@M
# gradient wrt the 3 free params t01,t02,t12: dA/dt01 = E01 - E10
print("dt01",G[0,1]-G[1,0],"dt02",G[0,2]-G[2,0],"dt12",G[1,2]-G[2,1])
